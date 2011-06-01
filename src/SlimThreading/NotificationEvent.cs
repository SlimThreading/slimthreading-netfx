// Copyright 2011 Carlos Martins, Duarte Nunes
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

#pragma warning disable 0420

namespace SlimThreading {

    //
    // This value type implements a non-waitable notification event
    // that is used internally by the Slim Threading library.
    //

    internal struct NotificationEvent {

        //
        // The value of the *state* field when the event is signalled.
        //

        internal static readonly WaitBlock SET = WaitBlock.SENTINEL;

        //
        // The state of the event and the event's queue (i.e., a non-blocking
        // stack) are stored on the *state* field as follows:
        // - *state* == SET: the event is signalled;
        // - *state* == null: the event is non-signalled the queue is empty;
        // - *state* != null && *state* != SET: the event is non-signalled
        //                                      and its queue is non-empty.
        //

        internal volatile WaitBlock state;

        //
        // The number of spin cycles executed by the first waiter
        // thread before it blocks on the park spot.
        //

        internal readonly int spinCount;

        internal NotificationEvent(bool initialState, int sc) {
            state = initialState ? SET : null;
            spinCount = Platform.IsMultiProcessor ? sc : 0;
        }

        internal NotificationEvent(bool initialState) 
            : this(initialState, 0) { }

        //
        // Returns true if the event is set.
        //

        internal bool IsSet {
            get { return state == SET; }
        }

        //
        // Sets the event to the signalled state.
        //

        internal bool Set() {

            //
            // If the event is already signalled, return true.
            //

            if (state == SET) {
                return true;
            }

            //
            // Atomically signal the event and grab the wait queue.
            //

            WaitBlock p = Interlocked.Exchange(ref state, SET);

            //
            // If the event queue is empty, return the previous state of the event.
            //

            if (p == null || p == SET) {
                return p == SET;
            }

            //
            // If spinning is configured and there is more than one thread in the
            // wait queue, we first release the thread that is spinning. As only 
            // one thread spins, we maximize the chances of unparking that thread
            // before it blocks.
            //

            if (spinCount != 0 && p.next != null) {
                p.RemoveLast().TryLockAndUnpark();
            }

            //
            // Lock and unpark all waiting threads.
            //

            do {
                p.TryLockAndUnpark();
            } while ((p = p.next) != null);

            //
            // Return the previous state of the event.
            //

            return false;
        }

        //
        // Resets the event to the non-signalled state.
        //

        internal bool Reset() {
            return state == SET && Interlocked.CompareExchange(ref state, null, SET) == SET;
        }

        //
        // Waits until the event is signalled, activating the specified cancellers.
        //

        internal int Wait(StCancelArgs cargs) {
            return state == SET ? StParkStatus.Success
                 : cargs.Timeout == 0 ? StParkStatus.Timeout
                 : SlowWait(cargs, new WaitBlock(WaitType.WaitAny));
        }

        private int SlowWait(StCancelArgs cargs, WaitBlock wb) {
            do {

                //
                // If the event is now signalled, return success.
                //

                WaitBlock s;
                if ((s = state) == SET) {
                    return StParkStatus.Success;
                }

                wb.next = s;
                if (Interlocked.CompareExchange(ref state, wb, s) == s) {
                    break;
                }
            } while (true);

            //
            // Park the current thread, activating the specified cancellers and spinning
            // if appropriate.
            //

            int ws = wb.parker.Park(wb.next == null ? spinCount : 0, cargs);

            //
            // If the wait was cancelled, unlink the wait block from the
            // event's queue.
            //

            if (ws != StParkStatus.Success) {
                Unlink(wb);
            }

            return ws;
        }

        //
        // Waits until the event is signalled, using the specified parker object.
        //

        internal WaitBlock WaitWithParker(StParker pk, WaitType type, int key, ref int sc)
        {
            WaitBlock wb = null;
            do {
                WaitBlock s;
                if ((s = state) == SET) {

                    //
                    // The event is signalled. Try to lock it and self unpark the current thread. 
                    // Anyway, return null to signal that no wait block was queued.
                    //

                    if (pk.TryLock()) {
                        pk.UnparkSelf(key);
                    }
                    return null;
                }

                //
                // The event seems closed; so, if this is the first loop iteration,
                // create a wait block.
                //

                if (wb == null) {
                    wb = new WaitBlock(pk, type, 0, key);
                }

                //
                // Try to insert the wait block in the event's queue, if the
                // event remains non-signalled.
                //

                wb.next = s;
                if (Interlocked.CompareExchange(ref state, wb, s) == s) {

                    //
                    // Return the inserted wait block and the suggested spin count.
                    //

                    sc = s == null ? spinCount : 0;
                    return wb;
                }
            } while (true);
        }

        //
        // Unlinks the wait block from the event's wait queue.
        //

        internal void Unlink(WaitBlock wb) {
            WaitBlock s;
            if ((s = state) == SET || s == null ||
                (wb.next == null && s == wb &&
                 Interlocked.CompareExchange(ref state, null, s) == s)) {
                return;
            }
            SlowUnlink(wb);
        }

        //
        // Slow path to unlink the wait block from the event's
        // wait queue.
        //

        void SlowUnlink(WaitBlock wb) {
            WaitBlock next;
            if ((next = wb.next) != null && next.parker.IsLocked) {
                next = next.next;
            }

            WaitBlock p = state;

            while (p != null && p != next && state != null && state != SET) {
                WaitBlock n;
                if ((n = p.next) != null && n.parker.IsLocked) {
                    p.CasNext(n, n.next);
                } else {
                    p = n;
                }
            }
        }
    }

    /// <summary>
    /// Notifies one or more waiting threads that an event has occurred.
    /// </summary>
    public abstract class StNotificationEventBase : StWaitable {

        internal NotificationEvent waitEvent;
        
        protected StNotificationEventBase(bool initialState, int sc) {
            id = NOTIFICATION_EVENT_ID;
            waitEvent = new NotificationEvent(initialState, sc);
        }

        protected StNotificationEventBase(bool initialState) 
            : this(initialState, 0) { }

        //
        // StWaitable methods.
        //

        //
        // Returns true if the event is signalled.
        //

        internal override bool _AllowsAcquire {
            get { return waitEvent.IsSet; }
        }

        //
        // Returns true if the event is signalled else return false.
        //

        internal override bool _TryAcquire() {
            return waitEvent.IsSet;
        }

        //
        // Executes the prologue of the Waitable.WaitAny method.
        //

        internal override WaitBlock _WaitAnyPrologue(StParker pk, int key,
                                                     ref WaitBlock hint, ref int sc) {
            return waitEvent.WaitWithParker(pk, WaitType.WaitAny, key, ref sc);
        }

        //
        // Executes the prologue of the Waitable.WaitAll method.
        //

        internal override WaitBlock _WaitAllPrologue(StParker pk, ref WaitBlock hint, ref int sc) {
            return waitEvent.WaitWithParker(pk, WaitType.WaitAll, StParkStatus.StateChange, ref sc);
        }

        //
        // Cancels the specified acquire attempt.
        //

        internal override void _CancelAcquire(WaitBlock wb, WaitBlock ignored) {
            waitEvent.Unlink(wb);
        }

        /*++
         * 
         * Specialized WaitAny and WaitAll methods.
         * 
         --*/

        /// <summary>
        /// Waits until one of the specified events is signalled, activating the
        /// specified cancellers. 
        /// </summary>
        /// <param name="evs">The array of events.</param>
        /// <param name="cargs">The cancellation arguments.</param>
        /// <returns>True if the wait succeed; false if timeout expired.</returns>
        public static int WaitAny(StNotificationEventBase[] evs, StCancelArgs cargs) {

            //
            // Validate the parameters.
            //
            // NOTE: We support null references on the *evs* array, provided
            //		 that the array contains at least a non-null entry.
            //

            int len = evs.Length;
            int def = 0;

            //
            // First, we scan the *evs* array checking is any of the specified
            // latches is open and computing the number of non-null references.
            //

            for (int i = 0; i < len; i++) {
                StNotificationEventBase ev = evs[i];
                if (ev != null) {
                    if (ev.waitEvent.IsSet) {

                        //
                        // The current event is signalled, so return success.
                        //

                        return i;
                    }
                    def++;
                }
            }

            //
            // If the *evs* array doesn't contain any non-null references,
            // throw ArgumentOutOfRangeException.
            //

            if (def == 0) {
                throw new ArgumentOutOfRangeException("evs", "The array is empty");
            }

            //
            // None of the specified events is signalled; so, return failure
            // if a null timeout was specified.
            //

            if (cargs.Timeout == 0) {
                return StParkStatus.Timeout;
            }

            //
            // Create a parker and execute the WaitPrologueWorker on all
            // events. We stop executing wait prologues as soon as we detect
            // that a latch is open.
            // 

            StParker pk = new StParker(1);
            WaitBlock[] wbs = new WaitBlock[len];
            int lv = -1;
            int sc = 0, gsc = 0;
            for (int i = 0; !pk.IsLocked && i < len; i++) {
                StNotificationEventBase ev = evs[i];
                if (ev != null) {
                    if ((wbs[i] = ev.waitEvent.WaitWithParker(pk, WaitType.WaitAny, i, ref sc)) == null) {
                        break;
                    }

                    //
                    // Adjust the global spin count.
                    //

                    if (gsc < sc) {
                        gsc = sc;
                    }
                    lv = i;
                }
            }

            //
            // Park the current thread, activating the specified cancellers
            // and spinning if appropriate.
            //

            int ws = pk.Park(gsc, cargs);

            //
            // Cancel the acquire attempt on all events where we executed the
            // wait prologue except the one where the we were woken.
            //

            for (int i = 0; i <= lv; i++) {
                if (i != ws) {
                    StNotificationEventBase ev;
                    if ((ev = evs[i]) != null) {
                        ev.waitEvent.Unlink(wbs[i]);
                    }
                }
            }

            //
            // If the WaitAny succeed, return success.
            //

            if (ws >= StParkStatus.Success && ws < len) {
                return ws;
            }

            //
            // The WaitAny failed, so report the failure appropriately.
            //

            StCancelArgs.ThrowIfException(ws);
            return StParkStatus.Timeout;
        }

        /// <summary>
        /// Waits until all the specified events are signalled, activating the
        /// specified cancellers.
        /// </summary>
        /// <param name="evs"></param>
        /// <param name="cargs"></param>
        /// <returns>True if the wait succeed; false if timeout expired.</returns>
        public static bool WaitAll(StNotificationEventBase[] evs, StCancelArgs cargs) {
            int len = evs.Length;
            int idx;

            for (idx = 0; idx < len; idx++) {
                StNotificationEventBase ev = evs[idx];
                if (ev == null) {
                    throw new ArgumentNullException();
                }
                if (!ev.waitEvent.IsSet) {
                    break;
                }
            }

            if (idx == len) {
                return true;
            }

            //
            // If the WaitAll can't be satisfied immediately and a null timeout was
            // specified, return failure.
            //

            if (cargs.Timeout == 0) {
                return false;
            }
                
            //
            // Create the wait block array and create a parker for cooperative release,
            // specifying as many releasers as the number of events.
            //

            WaitBlock[] wbs = new WaitBlock[len];
            StParker pk = new StParker(len);

            //
            // Execute the WaitPrologueWorker on all events.
            //

            int sc = 0, gsc = 1;
            for (int i = 0; i < len; i++) {
                if ((wbs[i] = evs[i].waitEvent.WaitWithParker(pk, WaitType.WaitAll,
                                                  StParkStatus.StateChange, ref sc)) != null) {

                    //
                    // Adjust the global spin count.
                    //

                    if (gsc != 0) {
                        if (sc == 0) {
                            gsc = 0;
                        } else if (sc > gsc) {
                            gsc = sc;
                        }
                    }
                }
            }

            //
            // Park the current thread, activating the specified cancellers
            // and spinning, if appropriate.
            //

            int ws = pk.Park(gsc, cargs);

            //
            // If all events are signalled, return success.
            //

            if (ws == StParkStatus.StateChange) {
                return true;
            }

            //
            // The wait was cancelled due to timeout, alert or thread interruption,
            // unlink the wait blocks on events where we actually inserted one.
            //

            for (int i = 0; i < len; i++) {
                WaitBlock wb = wbs[i];
                if (wb != null) {
                    evs[i].waitEvent.Unlink(wb);
                }
            }

            StCancelArgs.ThrowIfException(ws);
            return false;
        }
    }

    /// <summary>
    /// Notifies one or more waiting threads that an event has occurred.
    /// </summary>
    public sealed class StNotificationEvent : StNotificationEventBase {

        public StNotificationEvent(bool initialState, int spinCount) 
            : base(initialState, spinCount) { }

        public StNotificationEvent(bool initialState) 
            : this(initialState, 0) { }

        public StNotificationEvent() 
            : this(false, 0) { }


        //
        // Sets the event to the signalled state.
        //

        public bool Set() {
            return waitEvent.Set();
        }

        //
        // Resets the event to the non-signalled state.
        //

        public bool Reset() {
            return waitEvent.Reset();
        }

        //
        // Waits until the event is signalled, activating the specified cancellers.
        //

        public bool Wait(StCancelArgs cargs) {
            int ws = waitEvent.Wait(cargs);
            if (ws == StParkStatus.Success) {
                return true;
            }
            
            StCancelArgs.ThrowIfException(ws);
            return false;
        }

        public void Wait() {
            Wait(StCancelArgs.None);
        }

        //
        // Signals the event.
        //

        internal override bool _Release() {
            waitEvent.Set();
            return true;
        }
    }
}
