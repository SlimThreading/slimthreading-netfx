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
using System.Collections.Generic;

#pragma warning disable 0420

namespace SlimThreading {

    //
    // This class implements the parker.
    //

    public class StParker {

        //
        // Constants.
        //

        private const int WAIT_IN_PROGRESS_BIT = 31;
        private const int WAIT_IN_PROGRESS = (1 << WAIT_IN_PROGRESS_BIT);
        private const int LOCK_COUNT_MASK = (1 << 16) - 1;

        //
        // The link field used when the parker is registered
        // with an alerter.
        //

        internal volatile StParker pnext;

        //
        // The parker state.
        //

        internal volatile int state;

        //
        // The park spot used to block the parker's owner thread.
        //

        internal ParkSpot parkSpot;

        //
        // The park wait status.
        //

        internal int waitStatus;

        //
        // Constructors.
        //

        public StParker(int releasers) {
            state = releasers | WAIT_IN_PROGRESS;
        }

        public StParker() {
            state = 1 | WAIT_IN_PROGRESS;
        }

        //
        // Resets the parker.
        //

        public void Reset(int releasers) {
            pnext = null;
            state = releasers | WAIT_IN_PROGRESS;
        }

        public void Reset() {
            pnext = null;
            state = 1 | WAIT_IN_PROGRESS;
        }

        //
        // Tests and clears the wait-in-progress bit.
        //

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
        // Returns true if the parker is locked.
        //

        public bool IsLocked {
            get { return (state & LOCK_COUNT_MASK) == 0; }
        }

        //
        // Tries to lock the parker.
        //

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

        //
        // Tries to cancel the parker.
        //

        public bool TryCancel() {
            do {

                //
                // If the parker is already locked, return false.
                //

                int s;
                if (((s = state) & LOCK_COUNT_MASK) <= 0) {
                    return false;
                }

                //
                // Try to set the park's count down lock to zero,
                // preserving the wait-in-progress bit. If succeed,
                // return true.
                //
                if (Interlocked.CompareExchange(ref state, (s & WAIT_IN_PROGRESS),
                                                s) == s) {
                    return true;
                }
            } while (true);
        }

        //
        // Cancels the parker.
        //
        // NOTE: This method should be called only by the parker's
        //       owner thread.
        //

        public void SelfCancel() {
            state = WAIT_IN_PROGRESS;
        }

        //
        // Unparks the parker's owner thread if it is still with
        // its wait inprogress.
        //

        public bool UnparkInProgress(int ws) {
            waitStatus = ws;
            return (state & WAIT_IN_PROGRESS) != 0 &&
                   (Interlocked.Exchange(ref state, 0) & WAIT_IN_PROGRESS) != 0;
        }

        //
        // Unparks the parker owner thread.
        //

        public void Unpark(int status) {
            if (!UnparkInProgress(status)) {
                if (parkSpot != null) {
                    parkSpot.Set();
                } else {

                    //
                    // This is a callback parker.
                    // If a timer was used and it didn't fired, unlink the timer
                    // (whose parker is already locked) from the timer list.
                    // Finally, execute the associated callback.
                    //

                    CbParker cbpk = (CbParker)this;
                    if (cbpk.toTimer != null && status != StParkStatus.Timeout) {
                        TimerList.UnlinkRawTimer(cbpk.toTimer);
                        cbpk.toTimer = null;
                    }
                    cbpk.callback(status);
                }
            }
        }

        //
        // Unparks the parker's owner thread.
        //

        internal void UnparkSelf(int status) {
            waitStatus = status;
            state = 0;
        }

        //
        // Parks the current thread until it is unparked, activating the
        // specified cancellers and spinning if specified.
        //

        public int Park(int spinCount, StCancelArgs cargs) {

            //
            // Spin the specified number of cycles before blocking
            // the current thread.
            //

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

            //
            // Allocate a park spot to block the current thread.
            //

            parkSpot = ParkSpot.Alloc();

            //
            // Try to clear the wait-in-progress bit. If the bit was already
            // clear, the thread was unparked. So, free the park spot and
            // return the wait status.
            //

            if (!TestAndClearInProgress()) {
                parkSpot.Free();
                return waitStatus;
            }

            //
            // If an alerter was specified, register the parker with
            // the alerter, before block the thread on its park spot.
            //

            bool unregister = false;
            if (cargs.Alerter != null) {
                if (!(unregister = cargs.Alerter.RegisterParker(this))) {

                    //
                    // The alerter is already set. So, try to cancel the
                    // parker and, if succeed, free the park spot and return
                    // an alerted wait status.
                    //

                    if (TryCancel()) {
                        parkSpot.Free();
                        return StParkStatus.Alerted;
                    }

                    //
                    // We can't cancel the parker because someone else
                    // acquired the parker count down lock. So, we must
                    // wait unconditionally on the park spot until it
                    // is set.
                    //

                    cargs.ResetImplicitCancellers();
                }
            }

            //
            // Wait on the park spot.
            //

            parkSpot.Wait(this, cargs);

            //
            // Free the park spot and deregister the parker from the
            // alerter, if it was registered.
            //

            parkSpot.Free();
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

        //
        // Delays execution of the current thread sensing
        // the alerter if specified.
        //

        public static int Sleep(StCancelArgs cargs) {
            StParker pk = new StParker();
            int ws;
            StCancelArgs.ThrowIfException(ws = pk.Park(0, cargs));
            return ws;
        }

        //
        // CASes on the *pnext* field.
        //

        internal bool CasNext(StParker n, StParker nn) {
            return (pnext == n &&
                    Interlocked.CompareExchange<StParker>(ref pnext, nn, n) == n);
        }
    }

    //
    // The type delegate used with the callback parker.
    //

    internal delegate void ParkerCallback(int waitStatus);

    //
    // This class implements the callback parker.
    //

    internal class CbParker : StParker {
        internal RawTimer toTimer;
        internal ParkerCallback callback;

        //
        // Constructor.
        //

        internal CbParker(ParkerCallback pkcb) : base(1) {
            callback = pkcb;
        }

        //
        // Enables the unpark callback.
        //

        internal int EnableCallback(int timeout, RawTimer tmr) {
 
	        //
	        // If the unpark method was already called, return immeditely.
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
                if (toTimer != null) {
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
    
    internal class SentinelParker : StParker {
        internal SentinelParker() : base(0) { }
    }
}
