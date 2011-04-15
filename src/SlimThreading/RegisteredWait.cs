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

    public class StRegisteredWait {

        private const StParker INACTIVE = null;
        private static SentinelParker ACTIVE = new SentinelParker();

        private const int BUSY_SPINS = 100;

        //
        // Fields.
        //

        private volatile StParker state;
        private StWaitable waitable;
        private CbParker cbparker;
        private RawTimer toTimer;
        private int timeout;
        private int cbtid;
        private WaitBlock waitBlock;
        private WaitBlock hint;
	    private WaitOrTimerCallback callback;
	    private object cbState;
        private bool executeOnce;

        //
        // Executes the unpark callback.
        //

        private void UnparkCallback(int ws) {

            Debug.Assert(ws == StParkStatus.Success || ws == StParkStatus.Timeout ||
                         ws == StParkStatus.WaitCancelled);

            //
	        // If the registered wait was cancelled, cancel the acquire
            // attempt and return immediately.
	        //
        
            if (ws == StParkStatus.WaitCancelled) {
                waitable._CancelAcquire(waitBlock, hint);
		        return;
	        }

	        //
	        // Set state to *our busy* grabing the current state, and execute
	        // the unpark callback processing.
	        //

            StParker myBusy = new SentinelParker();
            StParker oldState = Interlocked.Exchange<StParker>(ref state, myBusy);

	        do {

		        //
		        // If the acquire operation was cancelled, cancel the acquire
		        // attempt on the waitable.
		        //

		        if (ws != StParkStatus.Success) {
                    waitable._CancelAcquire(waitBlock, hint);
		        }

		        //
		        // Execute the user callback routine.
		        //

                cbtid = Thread.CurrentThread.ManagedThreadId;
		        callback(cbState, ws == StParkStatus.Timeout);
                cbtid = 0;

		        //
		        // If the registered wait was configured to execute once or
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
		        // We must re-register with the Waitable.
		        // So, initialize the parker and execute the WaitAny prologue.
		        //

                cbparker.Reset(1);
                int ignored = 0;
                waitBlock = waitable._WaitAnyPrologue(cbparker, StParkStatus.Success, ref hint,
                                                      ref ignored);

		        //
		        // Enable the unpark callback.
		        //

                ws = cbparker.EnableCallback(timeout, toTimer);
                if (ws == StParkStatus.Pending) {

			        //
			        // If the *state* field constains still *my busy* set it to ACTIVE.
			        //

                    if (state == myBusy) {
                        Interlocked.CompareExchange<StParker>(ref state, ACTIVE, myBusy);
                    }
			        return;
		        }

		        //
		        // The waitable was already signalled. So, execute the unpark
		        // callback inline.
		        //

	        } while (true);
        }

        //
        // Constructor: registers a wait with a Waitable synchronizer.
        //

        internal StRegisteredWait(StWaitable waitObject,  WaitOrTimerCallback callback,
                                  object cbState, int timeout, bool executeOnce) {

        	//
	        // Validate the arguments.
	        //

            if (timeout == 0) {
                throw new ArgumentOutOfRangeException("\"timeout\" can't be zero");
            }
            if (callback == null) {
                throw new ArgumentOutOfRangeException("\"callback\" can't be null");
            }
            if ((waitObject is StReentrantFairLock) || (waitObject is StReentrantReadWriteLock)) {
                throw new InvalidOperationException("can't register waits on reentrant locks");
            }
            if ((waitObject is StNotificationEvent) && !executeOnce) {
                throw new InvalidOperationException("Notification event can't register waits"
                            + " to execute more than once");
            }

	        //
	        // Initialize the register wait fields
	        //

            waitable = waitObject;
            cbparker = new CbParker(UnparkCallback);
            toTimer = new RawTimer(cbparker);
	        this.timeout = timeout;
            this.executeOnce = executeOnce;
	        this.callback = callback;
	        this.cbState = (cbState != null) ? cbState : this;
        
            //
	        // Execute the WaitAny prologue on the waitable.
	        //
            int ignored = 0;
            waitBlock  = waitObject._WaitAnyPrologue(cbparker, StParkStatus.Success, ref hint,
                                                     ref ignored);

            //
	        // Set the registered wait state to active and enable the
            // unpark callback.
	        //

	        state = ACTIVE;
            int ws = cbparker.EnableCallback(timeout, toTimer);
            if (ws != StParkStatus.Pending) {
		
                //
		        // The acquire operation was already accomplished. To prevent
                // uncontrolled reentrancy, the unpark callback is executed inline.
		        //

		        UnparkCallback(ws);
	        }
        }

        //
        // Unregisters the registered wait.
        //

        public bool Unregister() {

	        //
	        // If the unregister is being called from the callback method,
	        // we just set the *onlyOnce* to TRUE and return.
	        //

	        if (cbtid == Thread.CurrentThread.ManagedThreadId) {
		        executeOnce = true;
		        return true;
	        }

	        //
	        // If the wait is registered, we try to unregister it, and return
	        // only after the wait is actually unregistered.
	        // If the wait was already unregistered or an unregister is taking
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
	        // Try to cancel the wait's callback parker. If we succeed, call
	        // the unpark with status disposed. Otherwise, a callback is taking
	        // place and we must synchronize with its end.
	        //

	        if (cbparker.TryCancel()) {
                cbparker.Unpark(StParkStatus.WaitCancelled);
	        } else {
                pk.Park(BUSY_SPINS, StCancelArgs.None);
	        }

	        //
	        // Set the registered wait object to inactive and return success.
	        //

	        state = INACTIVE;
	        return true;
        }
    }
}
