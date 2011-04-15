// Copyright 2011 Carlos Martins
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//  
using System;
using System.Threading;
using System.Diagnostics;

#pragma warning disable 0420

namespace SlimThreading {

    //
    // The delegate type used with registered takes.
    //

    public delegate void StTakeCallback<Q>(object state, Q dataItem, bool timedOut);

    public class StRegisteredTake<T> {

        private const StParker INACTIVE = null;
        private static SentinelParker ACTIVE = new SentinelParker();

        private const int BUSY_SPINS = 100;

        //
        // Fields.
        //

        private volatile StParker state;
        private StBlockingQueue<T> queue;
        private CbParker cbparker;
        private RawTimer toTimer;
        private int timeout;
        private int cbtid;
        public T dataItem;
        private StBlockingQueue<T>.WaitNode waitNode;
        private StBlockingQueue<T>.WaitNode hint;
        private StTakeCallback<T> callback;
        private object cbState;
        private bool executeOnce;

        //
        // Executes the unpark callback.
        //

        private void UnparkCallback(int ws) {

            Debug.Assert(ws == StParkStatus.Success || ws == StParkStatus.Timeout ||
                         ws == StParkStatus.TakeCancelled);

            //
            // If the registered take was cancelled, cancel the take attempt
            // and return immediately.
            //

            if (ws == StParkStatus.TakeCancelled) {
                queue.CancelTakeAttempt(waitNode, hint);
                return;
            }

            //
            // Set state to *our busy*, grabing the current state.
            //

            StParker myBusy = new SentinelParker();
            StParker oldState = Interlocked.Exchange<StParker>(ref state, myBusy);

            do {

                //
                // If the take operation succeeded, execute the take epilogue;
                // otherwise, cancel the take attempt.
                //

                if (ws == StParkStatus.Success) {
                    queue.TakeEpilogue();
                } else {
                    queue.CancelTakeAttempt(waitNode, hint);
                }

                //
                // Execute the user callback routine.
                //

                cbtid = Thread.CurrentThread.ManagedThreadId;
                callback(cbState, waitNode == null ? dataItem : waitNode.channel,
                         ws == StParkStatus.Timeout);
                cbtid = 0;

                //
                // If the registered take was configured to execute once or
                // there is an unregister in progress, set the state to INACTIVE.
                // If a thread is waiting to unregister, unpark it.
                //

                if (executeOnce || !(oldState is SentinelParker)) {
                    if (!(oldState is SentinelParker)) {
                        oldState.Unpark(StParkStatus.Success);
                    } else {
                        state = INACTIVE;
                    }
                    return;
                }

                //
                // We must re-register with the queue.
                // So, initialize the parker and execute the TakeAny prologue.
                //

                cbparker.Reset();
                waitNode = queue.TryTakePrologue(cbparker, StParkStatus.Success, out dataItem,
                                                 ref hint);

                //
                // Enable the unpark callback.
                //

                if ((ws = cbparker.EnableCallback(timeout, toTimer)) == StParkStatus.Pending) {

                    //
                    // If the *state* field is still *my busy* set it to ACTIVE;
                    // anyway, return.
                    //

                    if (state == myBusy) {
                        Interlocked.CompareExchange<StParker>(ref state, ACTIVE, myBusy);
                    }
                    return;
                }

                //
                // The take was already accomplished; so, to prevent uncontrolled
                // reentrancy execute the unpark callback inline.
                //

            } while (true);
        }

        //
        // Constructor: registers a take with a blocking queue.
        //

        internal StRegisteredTake(StBlockingQueue<T> queue, StTakeCallback<T> callback,
                                  object cbState, int timeout, bool executeOnce) {

            //
            // Validate the arguments.
            //

            if (timeout == 0) {
                throw new ArgumentOutOfRangeException("\"timeout\" can not be zero");
            }
            if (callback == null) {
                throw new ArgumentOutOfRangeException("\"callback\" must be specified");
            }

            //
            // Initialize the registered take fields.
            //

            this.queue = queue;
            cbparker = new CbParker(UnparkCallback);
            if ((this.timeout = timeout) != Timeout.Infinite) {
                toTimer = new RawTimer(cbparker);
            }
            this.executeOnce = executeOnce;
            this.callback = callback;
            this.cbState = cbState;

            //
            // Execute the TryTakePrologue prologue on the queue.
            //

            waitNode = queue.TryTakePrologue(cbparker, StParkStatus.Success, out dataItem,
                                             ref hint);

            //
            // Set the state to active and enable the unpark callback.
            //

            state = ACTIVE;
            int ws;
            if ((ws = cbparker.EnableCallback(timeout, toTimer)) != StParkStatus.Pending) {

                //
                // The take operation was already accomplished. To prevent
                // uncontrolled reentrancy, the unpark callback is executed inline.
                //

                UnparkCallback(ws);
            }
        }

        //
        // Unregisters the registered take.
        //

        public bool Unregister() {

            //
            // If the unregister is being called from the callback method,
            // we just set the *onlyOnce* to *true* and return.
            //

            if (cbtid == Thread.CurrentThread.ManagedThreadId) {
                executeOnce = true;
                return true;
            }

            //
            // If the take is registered, we try to unregister it, and return
            // only after the wait is actually unregistered.
            // If the take was already unregistered or an unregister is taking
            // place, this method returns false.
            //

            StSpinWait spinner = new StSpinWait();
            StParker pk = new StParker(0);
            StParker oldState;
            do {
                if ((oldState = state) == INACTIVE) {
                    return false;
                }
                if (oldState == ACTIVE) {
                    if (Interlocked.CompareExchange<StParker>(ref state, pk, ACTIVE) == ACTIVE) {
                        break;
                    }
                } else if (!(oldState is SentinelParker)) {
                    return false;
                }
                spinner.SpinOnce();
            } while (true);

            //
            // Try to cancel the callback parker. If we succeed, call the unpark
            // with status cancelled. Otherwise, a callback is taking place and
            // we must wait until its completion.
            //

            if (cbparker.TryCancel()) {
                cbparker.Unpark(StParkStatus.TakeCancelled);
            } else {
                pk.Park(BUSY_SPINS, StCancelArgs.None);
            }

            //
            // Set the registered take object to inactive and return success.
            //

            state = INACTIVE;
            return true;
        }
    }
}
