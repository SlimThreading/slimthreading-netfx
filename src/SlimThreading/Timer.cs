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

    public class StTimer : StWaitable {

        private const StParker INACTIVE = null;
        private static SentinelParker ACTIVE = new SentinelParker();
        private static SentinelParker SETTING = new SentinelParker();
 
        //
        // Fields.
        //

        private volatile StParker state;
        private CbParker cbparker;
        private RawTimer timer;
	    private int cbtid; 
	    private int dueTime;
	    private int period;
        private bool useDueTime;
        private WaitOrTimerCallback callback;
        private object cbState;
        private StWaitable tmrEvent;

        //
        // Constructors.
        //

        public StTimer(bool notificationTimer) {
            if (notificationTimer) {
                tmrEvent = new StNotificationEvent();
            } else {
                tmrEvent = new StSynchronizationEvent();
            }
 
            state = INACTIVE;
            cbparker = new CbParker(TimerCallback);
            timer = new RawTimer(cbparker);        
        }

        public StTimer() : this(true){}


        //
        // Executes the timer callback
        //

        internal void TimerCallback(int ws) {

            Debug.Assert(ws == StParkStatus.Timeout || ws == StParkStatus.TimerCancelled);
	
            //
	        // If the timer was cancelled, return immediately.
	        //

	        if (ws == StParkStatus.TimerCancelled) {
		        return;
	        }

	        //
	        // Set timer state to *our busy* state, grabing the current state and
	        // execute the timer callback processing.
	        //

            SentinelParker myBusy = new SentinelParker();
            StParker oldState = Interlocked.Exchange<StParker>(ref state, myBusy);

	        do {

		        //
		        // Signals the timer's event.
		        //

		        tmrEvent.Signal();
		
		        //
		        // Call the user-defined callback, if specified.
		        //

		        if (callback != null) {
			        cbtid = Thread.CurrentThread.ManagedThreadId;
			        callback(cbState, true);
			        cbtid = 0;
		        }

		        //
		        // If the  timer isn't periodic or if someone is trying to
                // cancel it, process cancellation.
		        //

		        if (period == 0 || !(oldState is SentinelParker)) {
			        if (!(oldState is SentinelParker)) {
				        oldState.Unpark(StParkStatus.Success);
                    } else {
				        state = INACTIVE;
			        }
			        return;
		        }

		        //
		        // Initialize the timer's parker.
		        //

                cbparker.Reset();
		        
                //
		        // Compute the timer delay and enable the unpark callback.
		        //

                int timeout;
		        if (useDueTime) {
			        timeout = dueTime;
			        useDueTime = false;
		        } else {
			        timeout = period | (1 << 31);
		        }
		        if ((ws = cbparker.EnableCallback(timeout, timer)) == StParkStatus.Pending) {
			        if (state == myBusy) {
				        Interlocked.CompareExchange<StParker>(ref state, ACTIVE, myBusy);
			        }
			        return;
		        }

		        //
		        // The timer already expired. So, execute the timer
                // callback inline.
		        //
	        } while (true);
        }

        //
        // Sets the timer.
        //

        public bool Set(int dueTime, int period, WaitOrTimerCallback callback, object cbState) {

	        //
	        // If the timer is being set from the user callback function,
	        // we just save the new settings and return success.
	        //

	        if (cbtid == Thread.CurrentThread.ManagedThreadId) {
		        this.dueTime = dueTime;
		        this.period = period;
		        useDueTime = true;
		        this.callback = callback;
		        this.cbState = cbState;
		        return true;
	        }

	        //
	        // The timer is being set externally. So, we must first cancel the
	        // timer. If the timer is already being set or cancelled, we return
	        // failure.
	        //

            StSpinWait spinner = new StSpinWait();
            StParker pk = new StParker(0);
            StParker oldState;
	        do {
		        if ((oldState = state) == INACTIVE) {
			        if (Interlocked.CompareExchange<StParker>(ref state, SETTING,
                                                              INACTIVE) == INACTIVE) {
				        goto SetTimer;
			        }
		        } else if (oldState == ACTIVE) {
			        if (Interlocked.CompareExchange<StParker>(ref state, pk, ACTIVE) == ACTIVE) {
				        break;
			        }
		        } else if (!(oldState is SentinelParker)) {
			        return false;
		        }
		        spinner.SpinOnce();
	        } while (true);

	        //
	        // Try to cancel the timer's callback parker. If succeed,
            // call the unpark method; otherwise, the timer callback is
            // taking place, so we must synchronize with its end.
	        //

	        if (cbparker.TryCancel()) {
		        cbparker.Unpark(StParkStatus.TimerCancelled);
	        } else {
		        pk.Park(100, StCancelArgs.None);
	        }

	        //
	        // Here, we know that the timer is cancelled and no one else
	        // is trying to set or cancel the timer.
	        // So, set the timer state to *our busy state* and start it with
	        // the new settings.
	        //

            state = SETTING;

        SetTimer:

	        this.dueTime = dueTime;
	        this.period = period;
	        useDueTime = false;
	        this.callback = callback;
	        this.cbState = cbState;
            StNotificationEvent nev = tmrEvent as StNotificationEvent;
            if (nev != null) {
                nev.Reset();
            } else {
                ((StSynchronizationEvent)tmrEvent).Reset();
            }

	        //
	        // Initialize the timer's parker, set the timer state to ACTIVE
            // and enable the unpark callback.
	        //

            cbparker.Reset();
            state = ACTIVE;
            int ws = cbparker.EnableCallback(dueTime, timer);
            if (ws != StParkStatus.Pending) {
		
		        //
		        // If the timer already fired or cancelled, call the unpark
		        // callback inline.
		        //

		        TimerCallback(ws);
	        }
	        return true;
        }

        //
        // Cancels the timer.
        //

        public bool Cancel() {

	        //
	        // If the timer is being cancelled from the user callback function,
	        // set the period to zero and return success. The timer will be
	        // cancelled on return from the callback function.
	        //

	        if (cbtid == Thread.CurrentThread.ManagedThreadId) {
		        period = 0;
		        return true;
	        }

	        //
	        // If the timer is active we must try to cancel the timer
	        // and return only after the timer is actually cancelled.
        	// if the timer is already inactive or a cancel or a set is taking
	        // place, this function returns FALSE. 
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
		        } if (!(oldState is SentinelParker)) {
			        return false;
		        }
		        spinner.SpinOnce();
	        } while (true);

	        //
	        // Try to cancel the timer's callback parker. If we can't
            // park the current thread until the timer callback finishes
            // execution.
	        //

	        if (cbparker.TryCancel()) {
		        cbparker.Unpark(StParkStatus.TimerCancelled);
	        } else {
                pk.Park(100, StCancelArgs.None);
            }

	        //
	        // Set the timer state to inactive and return success.
	        //

	        state = INACTIVE;
	        return true;
        }


        /*++
         * 
         * Waitable implementation.
         * 
         --*/

        //
        // Returns true if the associated event is signalled.
        //

        internal override bool _AllowsAcquire {
            get { return tmrEvent._AllowsAcquire; }
        }

        //
        // Returns true if the acquire operation succeeded.
        //

        internal override bool _TryAcquire() {
            return tmrEvent._TryAcquire();
        }

        //
        // Signals the event.
        //

        internal override bool _Release() {
            return tmrEvent._Release();
        }

        //
        // Executes the prologue of the Waitable.WaitAny method.
        //

        internal override WaitBlock _WaitAnyPrologue(StParker pk, int key,
                                                     ref WaitBlock hint, ref int sc) {
            return tmrEvent._WaitAnyPrologue(pk, key, ref hint, ref sc);
        }

        //
        // Executes the prologue of the Waitable.WaitAll method.
        //

        internal override WaitBlock _WaitAllPrologue(StParker pk, ref WaitBlock hint,
                                                     ref int sc) {
            return tmrEvent._WaitAllPrologue(pk, ref hint, ref sc);
        }

        //
        // Cancels the specified acquire attempt.
        //

        internal override void _CancelAcquire(WaitBlock wb, WaitBlock hint) {
            tmrEvent._CancelAcquire(wb, hint);
        }
    }
}
