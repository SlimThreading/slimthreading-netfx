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
using System.Linq;
using System.Threading;
using SlimThreading;

namespace Tests {
    public class TestWorkStealingQueue {
        private const int STEALERS = 19;
        private const int PUSHES = 100;

        private static readonly StWorkStealingQueue<object> wsq = new StWorkStealingQueue<object>();

        private static volatile bool running = true;
        private static readonly StCountDownEvent done = new StCountDownEvent(STEALERS + 1);

        private static long localPushes;
        private static long localPushTime;
        private static long localPops;
        private static long localFailedPops;
        private static long localPopTime;
        private static long localRemoves;
        private static long localFailedRemoves;
        private static long localRemoveTime;

        private static readonly long[] steals = new long[STEALERS];
        private static readonly long[] missedSteals = new long[STEALERS];
        private static readonly long[] failedSteals = new long[STEALERS];
        private static readonly long[] stealTime = new long[STEALERS];

        static void OwnerThread(object arg) {
            Console.WriteLine("+++ owner thread started...");

            var data = new object[PUSHES];
            int runs = 0;

            while (running) {
                int start = Environment.TickCount;

                for (int i = 0; i < PUSHES; ++i) {
                    wsq.LocalPush(data[i] = new object());
                    localPushes++;
                }

                localPushTime += Environment.TickCount - start;

                if (!running) {
                    break;
                }

                bool p = runs % 2 == 0;
                for (int i = p ? PUSHES - 1 : 0; p ? i >= 0 : i < PUSHES; i += p ? -1 : 1) {
                    start = Environment.TickCount;

                    object obj;
                    if ((i % 2) == 0) {
                        if (wsq.LocalPop(out obj)) {
                            localPops++;
                        } else {
                            localFailedPops++;
                        }

                        localPopTime += Environment.TickCount - start;
                    } else {
                        obj = data[i];
                        if (wsq.LocalRemove(obj)) {
                            localRemoves++;
                        } else {
                            localFailedRemoves++;
                        }
                        localRemoveTime += Environment.TickCount - start;
                    }
                }

                ++runs;
            }

            done.Signal();
            Console.WriteLine("+++ owner exiting, after {0} pushes, {1} pops, {2} removes.",
                                      localPushes, localPops, localRemoves);
        }

        static void StealerThread(object arg) {
            int n = (int)arg;
            Console.WriteLine("+++ stealer {0} thread started...", n);

            var spinWait = new SpinWait();
            bool missedSteal = false;

            while (running) {
                int start = Environment.TickCount;

                object data;
                if (wsq.TrySteal(out data, ref missedSteal)) {
                    steals[n]++;
                } else if (missedSteal) {
                    missedSteals[n]++;
                } else {
                    failedSteals[n]++;
                }
                
                stealTime[n] += Environment.TickCount - start;

                missedSteal = false;

                for (int i = 0; i < n; ++i) {
                    spinWait.SpinOnce();
                }
            }

            done.Signal();
            Console.WriteLine("+++ stealer {0} exiting, after {1} steals and {2} missed steals.",
                                       n, steals[n], missedSteals[n]);
        }

        public static Action Run() {
            var owner = new Thread(OwnerThread) { IsBackground = false, Name = "owner thread" };
            var stealers = new Thread[STEALERS];

            int g0Collects = GC.CollectionCount(0);
            int g1Collects = GC.CollectionCount(1);
            int g2Collects = GC.CollectionCount(2);

            long memoryFootprint = GC.GetTotalMemory(true);

            owner.Start();

            for (int i = 0; i < STEALERS; i++) {
                (stealers[i] = new Thread(StealerThread) {
                    Name = string.Format("stealer thread {0}", i)
                }).Start(i);
            }

            return () => {
                running = false;
                done.WaitOne();

                long totalSteals = steals.Sum();
                long totalFailedSteals = failedSteals.Sum();
                long totalMissedSteals = missedSteals.Sum();
                long totalStealTime = stealTime.Sum();
                long totalremoves = totalSteals + localPops + localRemoves;

                g0Collects = GC.CollectionCount(0) - g0Collects;
                g1Collects = GC.CollectionCount(1) - g1Collects;
                g2Collects = GC.CollectionCount(2) - g2Collects;
                memoryFootprint = GC.GetTotalMemory(true) - memoryFootprint;

                Console.WriteLine();

                Console.WriteLine("---local: {0} pushes, unit cost: {1} ns", localPushes,
                                  (int)((localPushTime * 1000000.0) / localPushes));
                Console.WriteLine("---local: {0} pops, unit cost: {1} ns", localPops,
                                  (int)((localPopTime * 1000000.0) / localPops));
                Console.WriteLine("---local: {0} failed pops", localFailedPops);
                Console.WriteLine("---local: {0} removes, unit cost: {1} ns", localRemoves,
                                  (int)((localRemoveTime * 1000000.0) / localRemoves));
                Console.WriteLine("---local: {0} failed removes", localFailedRemoves);
                Console.WriteLine("---foreign: {0} steals, unit cost: {1} ns", totalSteals,
                                  (int)((totalStealTime * 1000000.0) / totalSteals));
                Console.WriteLine("---foreign: {0} failed steals", totalFailedSteals);
                Console.WriteLine("---foreign: {0} missed steals", totalMissedSteals);

                Console.WriteLine("\n---collects: gen0={0}, gen1={1}, gen2={2}", g0Collects, g1Collects, g2Collects);
                Console.WriteLine("---memory footprint = {0}", memoryFootprint);

                Console.WriteLine("\n---inserts: {0}", localPushes);
                Console.WriteLine("---removes: {0}", totalremoves);
                Console.WriteLine("---expected items in queue: {0}", localPushes - totalremoves);
                Console.WriteLine("---items in queue: {0}", wsq.Count);

                Assert.AreEqual((int)(localPushes - totalremoves), wsq.Count);
            };
        }                                             
    }
}