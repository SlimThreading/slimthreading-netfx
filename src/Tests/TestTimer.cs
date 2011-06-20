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

    class TestTimer {

        //
        // The number of controller threads.
        //

        private const int THREADS = 1;

        //
        // Number of timers used for each controller thread.
        //

        private const int TIMERS = 100;

        //
        // The alerter and the count down latch used for shutdown.
        //

        static StAlerter shutdown = new StAlerter();
        static StCountDownEvent done = new StCountDownEvent(THREADS);


        //
        // Timer controller thread.
        //

        class TimerController {
            private int id;

            internal void Start(int tid) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = "timer ctrl " + id;
                t.Start();
            }

            //
            // The timer callback.
            //

            private static void TimerCallback(object context, bool timedOut) {
                //StTimer timer = (StTimer)context;
                //timer.Cancel();
                Console.Write('+');
            }

            private void Run() {
                int count = 0;
	            int failed = 0;

	            Console.WriteLine("+++ ctrl #{0} starts...\n", id);
	            Random rnd = new Random(Environment.TickCount * (id + 1));
                StTimer[] timers = new StTimer[TIMERS];
                for (int i = 0; i < TIMERS; i++) {
		            timers[i] = new StTimer(true);
	            }       

	            do {

		            //
		            // Start the timers.
		            //

		            int maxTime = 0;
		            for (int i = 0; i < TIMERS; i++) {
			            int delay = 100 + rnd.Next(500);
			            timers[i].Set(delay, 0, TimerCallback, timers[i]);
			            if (delay > maxTime) {
				            maxTime = delay;
			            }
		            }

		            int start = Environment.TickCount;
                    try {
                        int timeout = 350 + rnd.Next(500);
                        if (StWaitable.WaitAll(timers, new StCancelArgs(timeout, shutdown))) {
                            int elapsed = Environment.TickCount - start;
                            Console.WriteLine("+++ ctrl #{0}, synchronized with its timers[{1}/{2}]\n",
                                              id, maxTime, elapsed);
                            count++;
                            Thread.Sleep(100);
                            if (!shutdown.IsSet) {
                                continue;
                            }
                        } else {
                            int elapsed = Environment.TickCount - start;
                            Console.WriteLine("--- ctrl #{0}, timed out({1}) expired[{2}/{3}]\n",
                                                id, timeout, maxTime, elapsed);
                        }
                    } catch (StThreadAlertedException) {
                        Console.WriteLine("--- ctrl #{0}, CANCELLED\n", id);
                    }

                    //
                    // Cancel the timers.
                    //

                 
			        for (int i = 0; i < TIMERS; i++) {
				        timers[i].Cancel();
			        }
                    failed++;
	            } while (!shutdown.IsSet);
                for (int i = 0; i < TIMERS; i++) {
                    timers[i].Cancel();
                }                
                Console.WriteLine("+++ ctrl #{0} exiting after [{1}/{1}] synchs...\n",
                                  id, count, failed);
	            done.Signal();
            }
        }

        //
        // Starts the test.
        //

        internal static Action Run() {
            for (int i = 0; i < THREADS; i++) {
                new TimerController().Start(i);
            }
            int start = Environment.TickCount;
            return () => {
                shutdown.Set();
                int elapsed = Environment.TickCount - start;
                done.WaitOne();
            };
        }
    }
}
