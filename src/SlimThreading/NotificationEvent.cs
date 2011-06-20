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

using System.Threading;

#pragma warning disable 0420

namespace SlimThreading {

    //
    // This value type implements a non-waitable notification event
    // that is used internally by the SlimThreading library.
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

        internal bool IsSet {
            get { return state == SET; }
        }

        internal bool Set() {
            if (state == SET) {
                return true;
            }

            WaitBlock p = Interlocked.Exchange(ref state, SET);

            //
            // If the event queue is empty, return the previous state of the event.
            //

            if (p == null || p == SET) {
                return p == SET;
            }

            StParker pk;

            //
            // If spinning is configured and there is more than one thread in the
            // wait queue, we first release the thread that is spinning. As only 
            // one thread spins, we maximize the chances of unparking that thread
            // before it blocks.
            //

            if (spinCount != 0 && p.next != null) {
                WaitBlock pv = p;
                WaitBlock n;

                while ((n = pv.next).next != null) {
                    pv = n;
                }

                pv.next = null;

                if ((pk = n.parker).TryLock()) {
                    pk.Unpark(n.waitKey);
                }
            }

            do {
                if ((pk = p.parker).TryLock()) {
                    pk.Unpark(p.waitKey);
                }
            } while ((p = p.next) != null);

            //
            // Return the previous state of the event.
            //

            return false;
        }

        internal bool Reset() {
            return state == SET && Interlocked.CompareExchange(ref state, null, SET) == SET;
        }

        internal int Wait(StCancelArgs cargs) {
            return state == SET ? StParkStatus.Success
                 : cargs.Timeout == 0 ? StParkStatus.Timeout
                 : SlowWait(cargs, new WaitBlock(WaitType.WaitAny));
        }

        private int SlowWait(StCancelArgs cargs, WaitBlock wb) {
            do {
                WaitBlock s;
                if ((s = state) == SET) {
                    return StParkStatus.Success;
                }

                wb.next = s;
                if (Interlocked.CompareExchange(ref state, wb, s) == s) {
                    break;
                }
            } while (true);

            int ws = wb.parker.Park(wb.next == null ? spinCount : 0, cargs);

            if (ws != StParkStatus.Success) {
                Unlink(wb);
            }

            return ws;
        }

        internal WaitBlock WaitWithParker(StParker pk, WaitType type, int key, ref int sc) {
            WaitBlock wb = null;
            do {
                WaitBlock s;
                if ((s = state) == SET) {
                    return null;
                }

                if (wb == null) {
                    wb = new WaitBlock(pk, type, 0, key);
                }

                wb.next = s;
                if (Interlocked.CompareExchange(ref state, wb, s) == s) {
                    sc = s == null ? spinCount : 0;
                    return wb;
                }
            } while (true);
        }

        internal void Unlink(WaitBlock wb) {

            //
            // We can return immediately if the queue is empty or if the WaitBlock
            // is the only one in it and we succeeded in removing it. 
            //

            WaitBlock s;
            if ((s = state) == SET || s == null || (wb.next == null && s == wb &&
                Interlocked.CompareExchange(ref state, null, s) == s)) {
                return;
            }
            SlowUnlink(wb);
        }

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

        internal override bool _AllowsAcquire {
            get { return waitEvent.IsSet; }
        }

        internal override bool _TryAcquire() {
            return waitEvent.IsSet;
        }

        internal override WaitBlock _WaitAnyPrologue(StParker pk, int key,
                                                     ref WaitBlock hint, ref int sc) {
            return waitEvent.WaitWithParker(pk, WaitType.WaitAny, key, ref sc);
        }

        internal override WaitBlock _WaitAllPrologue(StParker pk, ref WaitBlock hint, ref int sc) {
            return waitEvent.WaitWithParker(pk, WaitType.WaitAll, StParkStatus.StateChange, ref sc);
        }
        
        internal override void _CancelAcquire(WaitBlock wb, WaitBlock ignored) {
            waitEvent.Unlink(wb);
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

        internal override bool _Release() {
            waitEvent.Set();
            return true;
        }
    }
}
