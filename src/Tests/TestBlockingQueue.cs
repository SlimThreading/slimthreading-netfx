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
	class TestBlockingQueue {

        //
        // The number of threads.
        //

		private const int PRODUCERS = 5;
		private const int CONSUMERS = 10;

        //
        // The queue.
        //

		private static StBlockingQueue<int> queue;

        //
        // The alerter and the count down latch used for shutdown.
        //

        private static StAlerter shutdown = new StAlerter();
        private static StCountDownEvent done = new StCountDownEvent(PRODUCERS + CONSUMERS);

	    //
        // The counters.
        //

		private static int[] productions = new int[PRODUCERS];
	    private static int[] consumptions = new int[CONSUMERS];

        //
        // The producer thread.
        //

        class Producer {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                VConsole.WriteLine("+++ p #{0} started...", id);
                int msg = 0;
                int fails = 0;
                do {
                    try {
                        if (++msg == 0) {
                            msg = 1;
                        }
                        // while (!queue.TryAdd(msg, new StCancelArgs(1, shutdown))) {
                        while (StBlockingQueue<int>.TryAddAny(new StBlockingQueue<int>[] { queue },
                                     msg, new StCancelArgs(1, shutdown)) == StParkStatus.Timeout) {
                            fails++;
                        }
                        if ((++productions[id] % 20000) == 0) {
                            VConsole.Write("-p{0}", id);
                        }
                    } catch (StThreadAlertedException) { }
                    //Thread.Sleep(0);
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ p #{0} exiting: [{1}/{2}]",
                                   id, productions[id], fails);
                done.Signal();
            }
        }

        //
        // The consumer thread.
        //

        class Consumer {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                VConsole.WriteLine("+++ c #{0} started...", id);
                int fail = 0;
                int rmsg;
                do {
                    try {
                        while (!queue.TryTake(out rmsg, new StCancelArgs(1, shutdown))) {
                            //while (StBlockingQueue<int>.TryTakeAny(new StBlockingQueue<int>[]{queue},
                            //           out rmsg, new StCancelArgs(1, shutdown)) == StParkStatus.Timeout) {
                            fail++;
                        }
                        if ((++consumptions[id] % 20000) == 0) {
                            VConsole.Write("-c{0}", id);
                        }
                    } catch (StThreadAlertedException) {
                        while (queue.TryTake(out rmsg, new StCancelArgs(20))) {
                            consumptions[id]++;
                        }
                        break;
                    }
                } while (true);
                VConsole.WriteLine("+++ c #{0} exiting: [{1}/{2}]",
                                  id, consumptions[id], fail);
                done.Signal();
            }
        }

        //
        // Starts the test.
        //

        internal static Action Run() {
            switch (new Random(Environment.TickCount).Next(3)) {
                case 0:
                    queue = new StUnboundedBlockingQueue<int>(true);
                    break;
                case 1:
                    queue = new StBoundedBlockingQueue<int>(4 * 1024, true);
                    break;
                default:
                    queue = new StArrayBlockingQueue<int>(4 * 1024, true);
                    break;
            }
            for (int i = 0; i < CONSUMERS; i++) {
                new Consumer().Start(i, "c #" + i);
            }
            for (int i = 0; i < PRODUCERS; i++) {
                new Producer().Start(i, "p #" + i);
            }

            Action stop = () => {
                shutdown.Set();
                done.Wait();
                long ps = 0, cs = 0;
                for (int i = 0; i < PRODUCERS; i++) {
                    ps += productions[i];
                }
                for (int i = 0; i < CONSUMERS; i++) {
                    cs += consumptions[i];
                }
                VConsole.WriteLine("+++ Total: prods = {0}, cons = {1}", ps, cs);
            };
            return stop;
        }
	}
}
