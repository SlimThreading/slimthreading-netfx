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
using System.Collections.Generic;

namespace SlimThreading {

    internal abstract class ParkSpot {

        //
        // Each thread has its own park spot factory.
        //

        [ThreadStatic]
        private static ParkSpotFactory factory;

        //
        // Returns the current thread's ParkSpot factory, ensuring appropriate
        // initialization.
        //

        internal static ParkSpotFactory Factory {
            get { return factory ?? (factory = new ParkSpotFactory()); }
        }

        //
        // As the access to a thread local field is expensive, we cache a
        // reference to the factory in the park spot instance in order to 
        // save the access when the park spot is freed.
        //

        protected readonly ParkSpotFactory _factory;

        protected ParkSpot(ParkSpotFactory psf) {
            _factory = psf;
        }

        //
        // Frees the park spot.
        //

        internal void Free() {
            _factory.Free(this);
        }

        //
        // Sets the park spot.
        //

        internal abstract void Set();

        //
        // Waits on the park spot, activating the specified cancellers.
        //

        internal abstract void Wait(StParker pk, StCancelArgs cargs);
    }

    //
    // This class represents a park spot based on an auto-reset 
    // event that will be used by normal threads (STA and MTA).
    //

    internal class EventBasedParkSpot : ParkSpot {

        private readonly AutoResetEvent psevent;

        internal EventBasedParkSpot(ParkSpotFactory psf) : base(psf) {
            psevent = new AutoResetEvent(false);
        }

        //
        // Waits on the park spot, activating the specified cancellers.
        //

        internal override void Wait(StParker pk, StCancelArgs cargs) {
            int ws;
            bool interrupted = false;
            do {
                try {
                    ws = psevent.WaitOne(cargs.Timeout, false) ? StParkStatus.Success
                                                               : StParkStatus.Timeout;
                    break;
                } catch (ThreadInterruptedException) {
                    if (cargs.Interruptible) {
                        ws = StParkStatus.Interrupted;
                        break;
                    }
                    interrupted = true;
                }
            } while (true);

            //
            // If the wait was cancelled due to an internal canceller, try
            // to cancel the park operation. If we fail, wait unconditionally
            // until the park spot is signalled.
            //

            if (ws != StParkStatus.Success) {
                if (pk.TryCancel()) {
                    pk.UnparkSelf(ws);
                } else {
                    if (ws == StParkStatus.Interrupted) {
                        interrupted = true;
                    }

                    do {
                        try {
                            psevent.WaitOne();
                            break;
                        } catch (ThreadInterruptedException) {
                            interrupted = true;
                        }
                    } while (true);
                }
            }

            //
            // If we were interrupted but can't return the *interrupted*
            // wait status, reassert the interrupt on the current thread.
            //

            if (interrupted) {
                Thread.CurrentThread.Interrupt();
            }
        }

        //
        // Sets the park spot.
        //

        internal override void Set() {
            psevent.Set();
        }
    }

    internal class ParkSpotFactory {

        private readonly Stack<ParkSpot> parkSpots;

        public ParkSpotFactory() {
            parkSpots = new Stack<ParkSpot>();
            parkSpots.Push(new EventBasedParkSpot(this));
        }

        //
        // Allocates a park spot to block the factory's owner thread.
        //

        internal ParkSpot Alloc() {
            return parkSpots.Count > 0 ? parkSpots.Pop() : new EventBasedParkSpot(this);
        }

        //
        // Frees the specified park spot.
        //

        internal void Free(ParkSpot ps) {
            parkSpots.Push(ps);
        }
    }
}
