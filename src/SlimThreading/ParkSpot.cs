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

namespace SlimThreading {

    //
    // This abstract class defines the functionality of the park spot.
    //

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

        private static ParkSpotFactory Factory {
            get {
                ParkSpotFactory f;
                if ((f = factory) == null) {
                    
                    //
                    // Create the park spot factory for the current thread.
                    //

                    ApartmentState apts = Thread.CurrentThread.GetApartmentState();
                    if (apts == ApartmentState.STA) {
                        f = new StaParkSpotFactory();
                    } else {

                        //
                        // If the thread has an Unknown apartment state, we set its
                        // apartmente state to MTA, preventing a change to STA after 
                        // we attach the thread's park spot factory.
                        //

                        if (apts == ApartmentState.Unknown) {
                            Thread.CurrentThread.SetApartmentState(ApartmentState.MTA);
                        }
                        f = new MtaParkSpotFactory();
                    }
                    factory = f;
                }
                return f;
            }
        }

        //
        // As the access to a thread local field is expensive, we cache a
        // reference to the factory in the park spot instance, in order to 
        // save one access when the park spot is freed.
        //

        protected readonly ParkSpotFactory _factory;

        //
        // Construtor.
        //

        protected ParkSpot(ParkSpotFactory psf) {
            _factory = psf;
        }

        //
        // Returns a park spot to block the current thread.
        //

        internal static ParkSpot Alloc() {
            return Factory.Alloc();
        }

        //
        // Frees this park spot.
        //

        internal void Free() {
            _factory.Free(this);
        }

        //
        // Waits on the park spot, activating the specified cancellers.
        //

        internal abstract void Wait(StParker pk, StCancelArgs cargs);

        //
        // Sets the park spot.
        //

        internal abstract void Set();

        //
        // Frees the native resources related with park spots.
        //

        internal static void FreeNativeResources() {
            Factory.FreeNativeResources();
        }
    }

    //
    // Auto-reset event based park spot that will be used by
    // normal threads (STA and MTA).
    //

    internal class EventBasedParkSpot : ParkSpot {

        //
        // The auto-reset event.
        //

        private readonly AutoResetEvent psevent;

        //
        // Contructor.
        //

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
                    interrupted = true;
                    if (cargs.Interruptible) {
                        ws = StParkStatus.Interrupted;
                        break;
                    }
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
   
    //
    // This abstract class defines the interface to a park spot factory.
    //

    internal abstract class ParkSpotFactory {

        //
        // Allocates a park spot to park the current thread.
        //

        internal abstract ParkSpot Alloc();

        //
        // Frees the specified park spot.
        //

        internal abstract void Free(ParkSpot ps);

        //
        // Frees the native resources associated with the park
        // spot factory.
        //
        // NOTE: This method is only called by the UMS worker threads.
        //

        internal virtual void FreeNativeResources() {}
    }

    //
    // This class implements the park spot factory for normal STA threads.
    //

    internal sealed class StaParkSpotFactory : ParkSpotFactory {

        //
        // The stack where we store the park spot of the factory
        // owner's thread.
        //

        private readonly Stack<ParkSpot> parkSpots;

        //
        // Constructor.
        //

        internal StaParkSpotFactory() {
            parkSpots = new Stack<ParkSpot>();
            parkSpots.Push(new EventBasedParkSpot(this));
        }

        //
        // Allocates a park spot to block the factory's owner thread.
        //

        internal override ParkSpot Alloc() {
            return (parkSpots.Count > 0) ? parkSpots.Pop() : new EventBasedParkSpot(this);
        }

        //
        // Frees a park spot.
        //

        internal override void Free(ParkSpot ps) {
            parkSpots.Push(ps);
        }
    }

    //
    // This class implements the park spot factory for normal MTA threads.
    //

    internal sealed class MtaParkSpotFactory : ParkSpotFactory {

        //
        // MTS thread use always the same event-based park spot.
        //

        private readonly EventBasedParkSpot parkSpot;

        //
        // Constructor.
        //

        internal MtaParkSpotFactory() {
            parkSpot = new EventBasedParkSpot(this);
        }

        //
        // Allocates a park spot to block the factory's owner thread.
        //

        internal override ParkSpot Alloc() {
            return parkSpot;
        }

        //
        // Frees a park spot.
        //

        internal override void Free(ParkSpot ps) {}
    }
}
