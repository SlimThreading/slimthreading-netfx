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

#pragma warning disable 0420

namespace SlimThreading {

    public class StParker {

        private const int WAIT_IN_PROGRESS_BIT = 31;
        private const int WAIT_IN_PROGRESS = (1 << WAIT_IN_PROGRESS_BIT);
        private const int LOCK_COUNT_MASK = (1 << 16) - 1;

        //
        // The link field used when the parker is registered
        // with an alerter.
        //

        internal volatile StParker pnext;

        internal volatile int state;
        internal IParkSpot parkSpot;
        internal int waitStatus;

        public StParker(int releasers, IParkSpot parkSpot) : this(releasers) {
            this.parkSpot = parkSpot;   
        }

        public StParker(IParkSpot parkSpot) : this(1, parkSpot) { }

        public StParker(int releasers) {
            state = releasers | WAIT_IN_PROGRESS;
        }

        public StParker() : this(1) { }

        public static int Sleep(StCancelArgs cargs) {
            int ws = new StParker().Park(0, cargs);
            StCancelArgs.ThrowIfException(ws);
            return ws;
        }

        public bool IsLocked {
            get { return (state & LOCK_COUNT_MASK) == 0; }
        }

        public void Reset(int releasers) {
            pnext = null;
            state = releasers | WAIT_IN_PROGRESS;
        }

        public void Reset() {
            Reset(1);
        }

        public bool TryLock() {
            do {

                //
                // If the parker is already locked, return false.
                //

                int s;
                if (((s = state) & LOCK_COUNT_MASK) == 0) {
                    return false;
                }

                //
                // Try to decrement the count down lock.
                //

                if (Interlocked.CompareExchange(ref state, s - 1, s) == s) {

                    //
                    // Return true if the count down lock reached zero.
                    //

                    return (s & LOCK_COUNT_MASK) == 1;
                }
            } while (true);
        }

        public bool TryCancel() {
            do {

                //
                // Fail if the parker is already locked.
                //

                int s;
                if (((s = state) & LOCK_COUNT_MASK) <= 0) {
                    return false;
                }

                //
                // Try to set the parker's count down lock to zero, preserving 
                // the wait-in-progress bit.
                //

                if (Interlocked.CompareExchange(ref state, (s & WAIT_IN_PROGRESS), s) == s) {
                    return true;
                }
            } while (true);
        }

        //
        // This method should be called only by the owner thread.
        //

        public void SelfCancel() {
            state = WAIT_IN_PROGRESS;
        }

        //
        // Unparks the parker's owner thread if the wait is still in progress.
        //

        public bool UnparkInProgress(int ws) {
            waitStatus = ws;
            return (state & WAIT_IN_PROGRESS) != 0 &&
                   (Interlocked.Exchange(ref state, 0) & WAIT_IN_PROGRESS) != 0;
        }

        public void Unpark(int status) {
            if (UnparkInProgress(status)) {
                return;
            }

            if (parkSpot != null) {
                parkSpot.Set();
            } else {

                //
                // This is a callback parker.
                // If a timer was used and it didn't fire, unlink the timer
                // (whose parker is already locked) from the timer list.
                // Finally, execute the associated callback.
                //

                var cbpk = (CbParker)this;
                if (cbpk.toTimer != null && status != StParkStatus.Timeout) {
                    TimerList.UnlinkRawTimer(cbpk.toTimer);
                    cbpk.toTimer = null;
                }
                cbpk.callback(status);
            }
        }

        //
        // This method should be called only by the owner thread.
        //

        public void UnparkSelf(int status) {
            waitStatus = status;
            state = 0;
        }

        public int Park(int spinCount, StCancelArgs cargs) {
            do {
                if (state == 0) {
                    return waitStatus;
                }
                if (cargs.Alerter != null && cargs.Alerter.IsSet && TryCancel()) {
                    return StParkStatus.Alerted;
                }
                if (spinCount-- <= 0) {
                    break;
                }
                Platform.SpinWait(1);
            } while (true);

            if (parkSpot == null) {
                parkSpot = ThreadExtensions.ForCurrentThread.ParkSpotFactory.Create();
            }

            //
            // Try to clear the wait-in-progress bit. If the bit was already
            // cleared, the thread is unparked. 
            //

            if (!TestAndClearInProgress()) {
                return waitStatus;
            }

            //
            // If an alerter was specified, we register the parker with
            // the alerter before blocking the thread on the park spot.
            //

            bool unregister = false;
            if (cargs.Alerter != null) {
                if (!(unregister = cargs.Alerter.RegisterParker(this))) {

                    //
                    // The alerter is already set. So, we try to cancel the parker.
                    //

                    if (TryCancel()) {
                        return StParkStatus.Alerted;
                    }

                    //
                    // We can't cancel the parker because someone else acquired 
                    // the count down lock. We must wait unconditionally until
                    // the park spot is set.
                    //

                    cargs = StCancelArgs.None;
                }
            }

            parkSpot.Wait(this, cargs);

            if (unregister) {
                cargs.Alerter.DeregisterParker(this);
            }
            return waitStatus;
        }

        public int Park(StCancelArgs cargs) {
            return Park(0, cargs);
        }

        public int Park() {
            return Park(0, StCancelArgs.None);
        }

        internal bool TestAndClearInProgress() {
            do {
                int s;
                if ((s = state) >= 0) {
                    return false;
                }
                if (Interlocked.CompareExchange(ref state, (s & ~WAIT_IN_PROGRESS), s) == s) {
                    return true;
                }
            } while (true);
        }

        //
        // CASes on the *pnext* field.
        //

        internal bool CasNext(StParker n, StParker nn) {
            return (pnext == n &&
                    Interlocked.CompareExchange(ref pnext, nn, n) == n);
        }
    }

    //
    // The delegate type used with the callback parker.
    //

    internal delegate void ParkerCallback(int waitStatus);

    //
    // This class implements the callback parker.
    //

    internal class CbParker : StParker {
        internal readonly ParkerCallback callback;
        internal RawTimer toTimer;

        internal CbParker(ParkerCallback pkcb) : base(1) {
            callback = pkcb;
        }

        //
        // Enables the unpark callback.
        //

        internal int EnableCallback(int timeout, RawTimer tmr) {
 
	        //
	        // If the unpark method was already called, return immediately.
	        //

	        if (state >= 0) {
		        return waitStatus;
	        }

            toTimer = null;
	        if (timeout == 0) {

		        //
		        // If a zero timeout is specified with a timer, we record the
                // current time as *fireTime* in order to support periodic timers.
		        //

                if (tmr != null) {
                    tmr.fireTime = Environment.TickCount;
                }

                if (TryCancel()) {
                    return StParkStatus.Timeout;
                }
            } else if (timeout != Timeout.Infinite) {
                toTimer = tmr;
                if (timeout < 0) {
                    TimerList.SetPeriodicRawTimer(tmr, timeout & ~(1 << 31));
                } else {
                    TimerList.SetRawTimer(tmr, timeout);
                }
            }

	        //
	        // Clear the wait-in-progress bit. If this bit is already cleared,
	        // the current thread was already unparked.
	        //
	
            if (!TestAndClearInProgress()) {
                if (toTimer != null && waitStatus != StParkStatus.Timeout) {
                    TimerList.UnlinkRawTimer(tmr);
                }
                return waitStatus;
            }
            return StParkStatus.Pending;
        }
    }

    //
    // This class implements a parker that is used as sentinel.
    //
    
    public class SentinelParker : StParker {
        public SentinelParker() : base(0) { }
    }
}
