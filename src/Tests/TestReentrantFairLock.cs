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

    class TestReentrantFairLock {

        //
        // The number of threads.
        //

        private const int THREADS = 5;

        //
        // The reentrant fair lock and the recursive acquisition count.
        //

        private const int REENTER = 5;
        static StReentrantFairLock flock = new StReentrantFairLock(200);

        //
        // The alerter and the count down latch used for shutdown.
        //

        static StAlerter shutdown = new StAlerter();
        static StCountDownEvent done = new StCountDownEvent(THREADS);

        //
        // The acquire counters.
        //

        static int[] counts = new int[THREADS];

        //
        // Shared random and acquire probability.
        //

        static volatile int sharedRandom;
        const int P = 25;

        //
        // Enter/exit thread.
        //

        class EnterExit {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                VConsole.WriteLine("+++ e/x #{0} started...", id);
                Random r = new Random((id + 1) * Environment.TickCount);
                int fail = 0;
                int localRandom = r.Next();
                do {
                    if ((localRandom % 100) < P) {
                        try {
                            while (!flock.WaitOne(new StCancelArgs(1, shutdown))) {
                                //while (!flock.WaitOne(new StCancelArgs(1, shutdown))) {
                                //while (StWaitable.WaitAny(new StWaitable[] { flock },
                                //                    new StCancelArgs(1, shutdown)) != StParkStatus.Success) {
                                //while (!StWaitable.WaitAll(new StWaitable[] { flock },
                                //                           new StCancelArgs(1, shutdown))) {

                                fail++;
                            }
                            for (int i = 1; i < REENTER; i++) {
                                flock.WaitOne();
                            }
                            localRandom = sharedRandom = r.Next();
                            for (int i = 0; i < REENTER; i++) {
                                flock.Exit();
                            }
                        } catch (StThreadAlertedException) {
                            break;
                        }
                    } else {
                        localRandom = r.Next();
                    }
                    if ((++counts[id] % 20000) == 0) {
                        VConsole.Write("-{0}", id);
                    }

                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ e/x #{0} exiting: [{1}/{2}]",
                                  id, counts[id], fail);
                done.Signal();
            }
        }

        //
        // Starts the test.
        //

        internal static Action Run() {
            for (int i = 0; i < THREADS; i++) {
                new EnterExit().Start(i, "e/x #" + i);
            }
            int start = Environment.TickCount;
            Action stop = () => {
                shutdown.Set();
                int elapsed = Environment.TickCount - start;
                done.WaitOne();
                long total = 0;
                for (int i = 0; i < THREADS; i++) {
                    total += counts[i];
                }

                VConsole.WriteLine("+++ Total: {0}, unit cost: {1} ns",
                                    total, (int)((elapsed * 1000000.0) / total));
            };
            return stop;
        }
    }
}
