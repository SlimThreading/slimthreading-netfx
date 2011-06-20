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
using System.Runtime.InteropServices;
using System.Threading;

#pragma warning disable 0420

namespace SlimThreading {

    //
    // This class implements a cyclic barrier.
    //

	public sealed class StBarrier {

        //
        // The state of each phase is held on an integer as follows:
        // bits 0..14: number of arrived partners;
        // bit 15: always 0;
        // bits 16..30: number of partners;
        // bit 31: always 0.
        //

        private const int MAX_PARTNERS = Int16.MaxValue;
        private const int ARRIVED_MASK = MAX_PARTNERS;
        private const int PARTNERS_SHIFT = 16;
        private const int PARTNERS_MASK = (MAX_PARTNERS << PARTNERS_SHIFT);

        //
        // The instances of this class hold the state of each barrier phase.
        //

        private sealed class PhaseState {
            internal volatile int state;
            internal NotificationEvent waitEvent;

            internal PhaseState(int initialState) {
                state = initialState;
                waitEvent = new NotificationEvent(false);
            }
        }

        //
        // The state of the current phase and its number.
        //

        private volatile PhaseState phState;
        private long phNumber;

        //
		// The post phase barrier action, its argument and the eventual
        // exception thrown by post phase action.
		//

		private readonly Action<object> pphAction;
        private readonly object pphActionContext;
        private Exception pphActionEx;

        //
        // Constructors.
        //

		public StBarrier(int partners, Action<object> ppha, object pphaCtx) {
            if (partners < 1 || partners > MAX_PARTNERS) {
                throw new ArgumentOutOfRangeException("\"partners\" is non-positive or greater 32767");
            }
            pphAction = ppha;
            pphActionContext = pphaCtx;
            phState = new PhaseState(partners << PARTNERS_SHIFT);
        }
		
        public StBarrier(int partners, Action<object> ppha) : this(partners, ppha, null) {}

        public StBarrier(int partners) : this(partners, null, null) { }

        //
        // Start a new phase.
        //

        private void NewPhase() {
            phNumber++;
            PhaseState phs = phState;
            phState = new PhaseState(phs.state & PARTNERS_MASK);
            phs.waitEvent.Set();
            return;
        }

        //
        // Finishes the current phase.
        //

        private void FinishPhase() {
            if (pphAction == null) {
                NewPhase();
                return;
            }
            try {

                //
                // Execute the post-phase action.
                //

                pphAction(pphActionContext);
                pphActionEx = null;
            } catch (Exception ex) {

                //
                // Capture the exception thrown by the post-phase action.
                //

                pphActionEx = ex;
                return;
            } finally {
                NewPhase();
                if (pphActionEx != null) {
                    throw new StBarrierPostPhaseException(pphActionEx);
                }
            }
        }

        //
        // Adds the specified number of partners to the current phase.
        //

        public long AddPartners(int count) {
		    if (count < 1 || count > MAX_PARTNERS)	{
			    throw new ArgumentOutOfRangeException("\"count\" non-positive or greater than 32767");
            }
            PhaseState phs = phState;
            do {
                int s;
                int partners = (s = phs.state) >> PARTNERS_SHIFT;
                int arrived = s & ARRIVED_MASK;

                //
                // Check if the maximum number of partners will be exceeded.
                //

                if (count + partners > MAX_PARTNERS) {
                    throw new ArgumentOutOfRangeException("Barrier partners overflow");
                }

                //
                // If the current phase is already closed, wait unconditionally
                // until the phase is started.
                //

                if (arrived == partners) {
                    phs.waitEvent.Wait(StCancelArgs.None);

                    //
                    // Get the new phase state and retry.
                    //

                    phs = phState;
                    continue;
                }

                //
                // Update the number of partners and return, if succeed.
                //

                int ns = s + (count << PARTNERS_SHIFT);
                if (Interlocked.CompareExchange(ref phs.state, ns, s) == s) {
                    return phNumber;
                }
            } while (true);
        }

        //
        // Adds a partner to the current barrier phase.
        //

        public long AddPartner() {
            return AddPartners(1);
        }

        //
        // Removes the specified number of partners.
        //

        public void RemovePartners(int count) {
            if (count < 1) {
                throw new ArgumentOutOfRangeException("\"count\" is less or equal to zero");
            }
            PhaseState phs = phState;
            do {
                int s, ns;
                int partners = (s = phs.state) >> PARTNERS_SHIFT;
                int arrived = s & ARRIVED_MASK;
                if (partners <= count) {
                    throw new ArgumentOutOfRangeException("\"count\" greater or equal to partners");
                }
                int np = partners - count;
                if (arrived == np) {
                    if (Interlocked.CompareExchange(ref phs.state, np << PARTNERS_SHIFT, s) == s) {
                        FinishPhase();
                        return;
                    }
                } else {
                    ns = (np << PARTNERS_SHIFT) | arrived;
                    if (Interlocked.CompareExchange(ref phs.state, ns, s) == s) {
                        return;
                    }
                }
            } while (true);
        }

        //
        // Removes one partner from the current phase of the barrier.
        //

        public void RemovePartner() {
            RemovePartners(1);
        }

        //
        // Signals the barrier and then waits until the current phase
        // completes, activating the specified cancellers.
        //

        public bool SignalAndWait(StCancelArgs cargs) {

            //
            // Get the current phase state.
            //

            PhaseState phs = phState;
            int s, partners, arrived;
            do {
                partners = (s = phs.state) >> PARTNERS_SHIFT;
                arrived = s & ARRIVED_MASK;
                if (arrived == partners) {
                    throw new InvalidOperationException("Barrier partners exceeded");
                }
                if (Interlocked.CompareExchange(ref phs.state, s + 1, s) == s) {
                    if (arrived + 1 == partners) {

                        //
                        // This is the last partner thread. So, finish the current
                        // phase and return success.
                        //

                        FinishPhase();
                        return true;
                    }
                    break;
                }
            } while (true);

            //
            // WaitOne on the phase event, activating the specified cancelers.
            //

            int ws = phs.waitEvent.Wait(cargs);

            //
            // If the event was signalled (i.e., successful synchronization),
            // return appropiately.
            //

            if (ws == StParkStatus.Success) {
                if (pphActionEx != null) {
                    throw new StBarrierPostPhaseException(pphActionEx);
                }
                return true;
            }

            //
            // The wait was cancelled. So, try to decrement the counter of
            // arrived partners on our phase, if that is still possible.
            //

            do {

                //
                // If our partners of our phase already arrived, we must wait
                // unconditionally on the phase's event, postpone the cancellation
                // and return normally.
                //

                partners = (s = phs.state) >> PARTNERS_SHIFT;
                arrived = s & ARRIVED_MASK;
                if (arrived == partners) {
                    phs.waitEvent.Wait(StCancelArgs.None);
                    StCancelArgs.PostponeCancellation(ws);
                    if (pphActionEx != null) {
                        throw new StBarrierPostPhaseException(pphActionEx);
                    }
                    return true;
                }

                //
                // Try to decrement the counter of arrived partners of our phase.
                //

                if (Interlocked.CompareExchange(ref phs.state, s - 1, s) == s) {

                    //
                    // We get out, so report the failure appropriately.
                    //

                    StCancelArgs.ThrowIfException(ws);
                    return false;
                }
            } while (true);
        }
        
        //
        // Signals and waits unconditionally.
        //

        public void SignalAndWait() {
            SignalAndWait(StCancelArgs.None);
        }

        //
        // Returns the current phase's number.
        //

        public long PhaseNumber {
            get { return phNumber; }
        }

        //
        // Returns the number of barrier's partners.
        //

        public int Partners {
            get {
                return (phState.state >> PARTNERS_SHIFT);
            }
        }

        //
        // Returns the number of partners remaining to synchronize
        // the current phase.
        //

        public int PartnersRemaining {
            get {
                int s;
                int partners = (s = phState.state) >> PARTNERS_SHIFT;
                return (partners - (s & ARRIVED_MASK));
            }
        }
    }
}
