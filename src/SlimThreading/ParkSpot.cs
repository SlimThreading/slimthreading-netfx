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
using System.Collections.Generic;
using System.Threading;

namespace SlimThreading {
    
    public interface IParkSpot {
        void Set();
        void Wait(StParker pk, StCancelArgs cargs);
    }

    public interface IParkSpotFactory {
        IParkSpot Create();
    }

    public interface IWaitBehavior {
        int Park(AutoResetEvent psevent, int timeout);
        int ParkerCancelled(int previousParkStatus);
        void ParkerNotCancelled(int previousParkStatus);
    }

    public class WaitOneBehavior : IWaitBehavior {
        public int Park(AutoResetEvent psevent, int timeout) {
            return psevent.WaitOne(timeout) ? StParkStatus.Success : StParkStatus.Timeout;
        }

        public int ParkerCancelled(int previousParkStatus) {
            return previousParkStatus;
        }

        public void ParkerNotCancelled (int previousParkStatus) { }
    }

    public class WaitAnyBehavior : IWaitBehavior {
        private readonly WaitHandle[] handles;
        private readonly int offset;

        public WaitAnyBehavior(WaitHandle[] handles, int offset) {
            Array.Copy(handles, 0, (this.handles = new WaitHandle[handles.Length + 1]), 1, handles.Length);
            this.offset = offset;
        }

        public int Park(AutoResetEvent psevent, int timeout) {
            handles[0] = psevent;
            int result = WaitHandle.WaitAny(handles, timeout);
            return result == WaitHandle.WaitTimeout ? StParkStatus.Timeout : result;
        }

        public int ParkerCancelled(int previousParkStatus) {
            if (previousParkStatus != StParkStatus.Timeout && previousParkStatus != StParkStatus.Interrupted) {
                previousParkStatus += offset - 1;
            }
            return previousParkStatus;
        }

        public void ParkerNotCancelled (int previousParkStatus) {
            if (previousParkStatus != StParkStatus.Timeout) {
                handles[previousParkStatus].UndoAcquire();  
            }
        }
    }

    public class WaitAllBehavior : WaitOneBehavior, IWaitBehavior {
        private readonly WaitHandle[] handles;

        public WaitAllBehavior(WaitHandle[] handles) {
            Array.Copy(handles, 0, (this.handles = new WaitHandle[handles.Length + 1]), 1, handles.Length);
        }

        public new int Park(AutoResetEvent psevent, int timeout) {
            handles[0] = psevent;
            return WaitHandle.WaitAll(handles, timeout) ? StParkStatus.Success : StParkStatus.Timeout;
        }
    }

    public class EventBasedParkSpotFactory : IParkSpotFactory {
        [ThreadStatic]
        private static EventBasedParkSpotFactory current;
        private static readonly IWaitBehavior DefaultWaitBehavior = new WaitOneBehavior();
        private readonly Stack<EventBasedParkSpot> parkSpots = new Stack<EventBasedParkSpot>();

        public static EventBasedParkSpotFactory Current {
            get { return  current ?? (current = new EventBasedParkSpotFactory()); }
        }

        public IParkSpot Create() {
            return Create(DefaultWaitBehavior);
        }

        public EventBasedParkSpot Create(IWaitBehavior waitBehavior) {
            return parkSpots.Count > 0 ? parkSpots.Pop() : new EventBasedParkSpot(this, waitBehavior);
        }

        public void Free(EventBasedParkSpot ps) {
            parkSpots.Push(ps);
        }
    }

    //
    // This class represents a park spot based on an auto-reset 
    // event that will be used by normal threads.
    //

    public class EventBasedParkSpot : IParkSpot {
        private readonly EventBasedParkSpotFactory factory;
        private readonly IWaitBehavior waitBehavior;
        private readonly AutoResetEvent psevent;

        internal EventBasedParkSpot(EventBasedParkSpotFactory factory, IWaitBehavior waitBehavior) {
            this.factory = factory;
            this.waitBehavior = waitBehavior;
            psevent = new AutoResetEvent(false);
        }

        public void Set() {
            psevent.Set();
        }

        public void Wait(StParker pk, StCancelArgs cargs) {
            bool interrupted = false;
            int lastTime = (cargs.Timeout != Timeout.Infinite) ? Environment.TickCount : 0;
            int ws;
            do {
                try {
                    ws = waitBehavior.Park(psevent, cargs.Timeout);
                    break;
                } catch (ThreadInterruptedException) {
                    if (cargs.Interruptible) {
                        ws = StParkStatus.Interrupted;
                        break;
                    }
                    interrupted = true;
                    cargs.AdjustTimeout(ref lastTime);
                }
            } while (true);

            //
            // If the wait was cancelled due to an internal canceller, try
            // to cancel the park operation. If we fail, wait unconditionally
            // until the park spot is signalled.
            //

            if (ws != StParkStatus.Success) {
                if (pk.TryCancel()) {
                    pk.UnparkSelf(waitBehavior.ParkerCancelled(ws));
                } else {
                    if (ws == StParkStatus.Interrupted) {
                        interrupted = true;
                    }

                    waitBehavior.ParkerNotCancelled(ws);

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

            factory.Free(this);
        }
    }
}
