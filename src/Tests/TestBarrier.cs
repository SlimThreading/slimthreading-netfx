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
using SlimThreading;

namespace TestShared {
    class TestBarrier {

        //
        // The number of threads.
        //

        private const int INITIAL_PARTNERS = 10;
        private const int TOTAL_PARTNERS = 10;

        //
        // The number of partners and the thread id.
        //

        private static int pcount;
        private static int tid;

        //
        // The barrier.
        //

        private static StBarrier barrier = new StBarrier(1, BarrierAction);

        //
        // The alerter and the count down latch used for shutdown.
        //

        private static StAlerter shutdown = new StAlerter();
        private static StCountDownEvent done = new StCountDownEvent(1);

        //
        // The number of synchronizations.
        //

        private static volatile int synchs;

        //
        // The barrier action.
        //

        static void BarrierAction(object ignored) {
            if ((++synchs % 1000) == 0) {
                VConsole.Write(".");
            }
        }

        //
        // The partner thread.
        //

        class PartnerThread {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                VConsole.WriteLine("+++ p #{0} started...", id);
                int fail = 0;
                int count = 0;
                do {
                    try {
                        while (!barrier.SignalAndWait(new StCancelArgs((id & 1) + 1, shutdown))) {
                            fail++;
                        }
                        count++;
                        if (id == 0 && (count % 10000) == 0) {
                            NewPartner();
                        } else if (id >= INITIAL_PARTNERS && count == id * 10000) {
                            if (RemovePartner()) {
                                break;
                            }
                        }
                    } catch (StThreadAlertedException) {
                        break;
                    }
                } while (true);
                VConsole.WriteLine("+++ p #{0} exiting: [{1}/{2}] synchs",
                                    id, count, fail);
                done.Signal();
            }
        }

        //
        // Adds a new partner if the maximum wasn't reached.
        //

        private static void NewPartner() {
            do {
                int c = pcount;
                if (c == TOTAL_PARTNERS) {
                    return;
                }
                if (Interlocked.CompareExchange(ref pcount, c + 1, c) == c) {
                    barrier.AddPartner();
                    done.TryAdd(1);
                    new PartnerThread().Start(Interlocked.Increment(ref tid) - 1,  "p #" + c);
                    return;
                }
            } while (true);
        }

        //
        // Removes a new partner if the minimum wasn't reached.
        //

        private static bool RemovePartner() {
            do {
                int c = pcount;
                if (c == INITIAL_PARTNERS) {
                    return false;
                }
                if (Interlocked.CompareExchange(ref pcount, c - 1, c) == c) {
                    barrier.RemovePartner();
                    return true;
                }
            } while (true);
        }

        //
        // Starts the test.
        //

        internal static Action Run() {
            pcount = 1;
            tid = 1;
            new PartnerThread().Start(0, "p #0");
            for (int i = 1; i < INITIAL_PARTNERS; i++) {
                NewPartner();
            }

            Action stop = () => {
                shutdown.Set();
                done.Wait();
                VConsole.WriteLine("++++ Total: {0}", synchs);
            };
            return stop;
        }
    }
}
