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

namespace SlimThreading {

    //
    // This class represents a raw timer.
    //

    internal class RawTimer {
        internal volatile RawTimer next;
        internal RawTimer prev;
        internal int delay;
        internal int fireTime;
        internal readonly StParker parker;

        //
        // The construtor.
        //

        internal RawTimer(StParker pk) {
            parker = pk;
        }
    }

    internal static class TimerList {

        private const int MAXIMUM_SLEEP_TIME = 30;

        private static SpinLock _lock;
        private static RawTimer timerListHead;
        private static RawTimer firedTimers;
        private static RawTimer limitTimer;
        private static int baseTime;
        private static StParker parker;

        //
        // Removes a timer from the timer list.
        //

        private static void RemoveTimerFromList(RawTimer tmr) {
            RawTimer next = tmr.next;
            RawTimer prev = tmr.prev;

            prev.next = next;
            next.prev = prev;
        }

        //
        // Inserts a timer at the tail of the list defined by *head*.
        //

        private static void InsertTailTimerList(RawTimer head, RawTimer tmr) {
            RawTimer tail = head.prev;

            tmr.next = head;
            tmr.prev = tail;
            tail.next = tmr;
            head.prev = tmr;
        }

        //
        // Inserts a timer at the head of the timer list.
        //

        private static void InsertHeadTimerList(RawTimer head, RawTimer tmr) {
            RawTimer first = head.next;
            
            tmr.next = first;
            tmr.prev = head;
            first.prev = tmr;
            head.next = tmr;
        }

        //
        // Adjusts the base time of the timer list.
        //

        private static void AdjustBaseTime() {
            int now;
            int sliding = (now = Environment.TickCount) - baseTime;

            RawTimer t;
            while ((t = timerListHead.next) != timerListHead) {
                if (t.delay <= sliding) {
                    sliding -= t.delay;
                    RemoveTimerFromList(t);

                    //
                    // Try to cancel the timer's parker. If we succeed, insert the
                    // raw timer on the fired timer list.
                    //

                    if (t.parker != null && t.parker.TryCancel()) {

                        //
                        // Add timer to the fired timer list, using the *prev* field.
                        //

                        t.prev = firedTimers;
                        firedTimers = t;
                    } else {

                        //
                        // Mark the raw timer as unlinked.
                        //

                        t.next = t;
                    }
                } else {
                    t.delay -= sliding;
                    break;
                }
            }
            baseTime = now;
        }

        //
        // Adds a raw timer to the timer list.
        //

        private static void SetRawTimerWorker(RawTimer timer, int delay) {

            //
	        // Set the next fire time.
        	//

	        timer.fireTime = baseTime + delay;

	        //
	        // Find the insertion position for the new timer.
	        //

            RawTimer t = timerListHead.next;
	        while (t != timerListHead) {
		        if (delay < t.delay) {
			        break;
		        }
		        delay -= t.delay;
		        t = t.next;
	        }

	        //
	        // Insert the new timer in the list, adjust the next timer and
	        // redefine the delay sentinel.
	        //

            timer.delay = delay;
            InsertTailTimerList(t, timer);
            if (t != timerListHead) {
                t.delay -= delay;
            }
        
            //
	        // If the timer was inserted at front of the timer list we need
	        // to wake the timer thread if it is blocked on its parker.
	        //

	        bool wake = (timerListHead.next == timer && parker.TryLock());

	        //
	        // Release the timer list lock and unpark the timer thread, if needed.
	        //

            _lock.Exit();
            if (wake) {
                parker.Unpark(StParkStatus.Success);
            }
        }

        //
        // Adds a raw timer to the timer list.
        //

        internal static void SetRawTimer(RawTimer tmr, int delay) {
            if (delay <= 0) {
                throw new ArgumentOutOfRangeException("\"delay\" must be greater than 0");
            }
	
            //
	        // Acquire the timer list lock and adjust the base time.
	        //

            _lock.Enter();
	        AdjustBaseTime();

	        //
	        // Set the timer and return.
	        //

	        SetRawTimerWorker(tmr, delay);
        }

        //
        // Adds a periodic raw timer to the timer list.
        //

        internal static void SetPeriodicRawTimer(RawTimer tmr, int period) {
            if (period <= 0) {
                throw new ArgumentOutOfRangeException("\"period\" must be greater than 0");
            }

	        //
	        // Acquire the timer list lock and adjust the base time.
	        //

            _lock.Enter();
	        AdjustBaseTime();

	        //
	        // Set the relative raw timer and return.
	        //

	        SetRawTimerWorker(tmr, (tmr.fireTime + period) - baseTime);
        }

        //
        // The timer thread.
        //

        private static void TimerThread() {

            //
            // The timer thread is a background thread.
            //

            Thread.CurrentThread.IsBackground = true;
            Thread.CurrentThread.Name = "StTimers";

            do {

		        //
		        // Acquire the timer list lock.
		        //

                _lock.Enter();

                //
                // If the limit timer was activated, remove it from the timer list.
                //

                if (limitTimer.next != limitTimer) {
                    if (limitTimer.next != timerListHead) {
                        limitTimer.next.delay += limitTimer.delay;
                    }
                    RemoveTimerFromList(limitTimer);
                    limitTimer.next = limitTimer;
                }

                //
                // Adjust the base time and the timer list and process
                // expired timers.
                //

		        AdjustBaseTime();

		        //
		        // Initializes the parker and compute the time at which the
		        // front timer must expire.
		        //

                parker.Reset(1);

                //
		        // If the first timer ...
		        //

                RawTimer first = timerListHead.next;
                int btime = baseTime;
                int delay;

        		if ((delay = first.delay) > MAXIMUM_SLEEP_TIME) {
                    limitTimer.delay = MAXIMUM_SLEEP_TIME;
			        InsertHeadTimerList(timerListHead, limitTimer);
                    if (first != timerListHead) {
                        first.delay -= MAXIMUM_SLEEP_TIME;
                    }
                    delay = MAXIMUM_SLEEP_TIME;
		        }

                //
                // Get the fired timers list and empty it.
                //

                RawTimer fired = TimerList.firedTimers;
                TimerList.firedTimers = null;

		        //
		        // Release the timer list's lock.
		        //

                _lock.Exit();

		        //
		        // Call unpark method on the expired timer's parkers.
		        //

                while (fired != null) {
                    RawTimer next = fired.prev;
                    fired.parker.Unpark(StParkStatus.Timeout);
                    fired = next;
                }

                //
		        // Since that the timer thread can take significant time to execute
		        // the timer's callbacks, we must ajust the *delay*, if needed.
		        //

                int sliding;
                if ((sliding = Environment.TickCount - btime) != 0) {
			        delay = (sliding >= delay) ? 0 : (delay - sliding);	
		        }

		        //
		        // Park the timer thread until the next timer expires or a new
		        // timer is inserted at front of the timer list.
		        //

		        parker.Park(new StCancelArgs(delay));
	        } while (true);

	        //
	        // We never get here!
	        //
        }

        //
        // Unlinks a cancelled raw timer from the timer list.
        //

        internal static void UnlinkRawTimer(RawTimer timer) {
	        if (timer.next != timer) {
                _lock.Enter();
		        if (timer.next != timer) {
                    if (timer.next != timerListHead) {
                        timer.next.delay += timer.delay;
                    }
			        RemoveTimerFromList(timer);
		        }
                _lock.Exit();
	        }
        }

        //
        // Tries to cancel a raw timer, if it's still active.
        //

        private static bool TryCancelRawTimer(RawTimer tmr) {
            if (tmr.parker.TryCancel()) {
		        UnlinkRawTimer(tmr);
                return true;
            }
            return false;
        }
    
        //
        // Initialize the timer list.
        //

        private const int TIMER_LIST_LOCK_SPINS = 200;

        static TimerList() {

            //
            // Create the objects.
            //

            _lock = new SpinLock(TIMER_LIST_LOCK_SPINS);
            timerListHead = new RawTimer(null);
            limitTimer = new RawTimer(null);
            parker = new StParker(1);

            //
            // Initialize the limit timer as unlinked.
            //
            
            limitTimer.next = limitTimer;

            //
            // Initialize the timer list as empty.
            //

            timerListHead.next = timerListHead.prev = timerListHead;

            //
            // Set the start base time e set the *delay* sentinel.
            //

        	baseTime = Environment.TickCount;
            timerListHead.delay = Int32.MaxValue;

	        //
	        // Create and start the timer thread.
	        //

            new Thread(TimerThread).Start();
	    }
    }
}

