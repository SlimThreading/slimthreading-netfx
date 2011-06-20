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
	class TestMultipleQueue {

		//
        // The number of threads.
        //

        private const int PRODUCERS = 2;
		private const int CONSUMERS = 4;

        //
        // The blocking queues.
        //

        private const int QUEUES = 10;
        private static StBlockingQueue<int>[] queues = new StBlockingQueue<int>[QUEUES];

        //
        // The alerter and the count down latch used for shutdown.
        //

		private static StAlerter shutdown = new StAlerter();
        private static StCountDownEvent done = new StCountDownEvent(PRODUCERS + CONSUMERS);

        //
        // The counters.
        //

		static int[] productions = new int[PRODUCERS];
		static int[] consumptions = new int[CONSUMERS];

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
                Random r = new Random(id);
                do {
                    try {
                        queues[r.Next(QUEUES)].TryAdd(msg, new StCancelArgs(shutdown));
                        if ((++productions[id] % 20000) == 0) {
                            VConsole.Write("-p{0}", id);
                        }
                        msg++;
                    } catch (StThreadAlertedException) {
                        break;
                    }
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ p #{0} exiting: [{1}]", id, productions[id]);
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
                int qi = 0;
                int rmsg;
                do {
                    try {
                        int ws;
                        while ((ws = StBlockingQueue<int>.TryTakeAny(queues, qi, queues.Length,
                                   out rmsg, new StCancelArgs(1, shutdown))) == StParkStatus.Timeout) {
                            fail++;
                        }
                        qi = ws;
                        if ((++consumptions[id] % 20000) == 0) {
                            VConsole.Write("-c{0}", id);
                        }
                    } catch (StThreadAlertedException) {
                        while (StBlockingQueue<int>.TryTakeAny(queues, 0, queues.Length,
                                    out rmsg, new StCancelArgs(100)) != StParkStatus.Timeout) {
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
            Random r = new Random(Environment.TickCount);
            for (int i = 0; i < QUEUES; i++) {
                switch (r.Next(3)) {
                    case 0:
                        queues[i] = new StUnboundedBlockingQueue<int>(false);
                        break;
                    case 1:
                        queues[i] = new StBoundedBlockingQueue<int>(32 * 1024, false);
                        break;
                    default:
                        queues[i] = new StArrayBlockingQueue<int>(32 * 1024, false);
                        break;
                }
            }
            for (int i = 0; i < CONSUMERS; i++) {
                new Consumer().Start(i, "c #" + i);
            }
            for (int i = 0; i < PRODUCERS; i++) {
                new Producer().Start(i, "p #" + i);
            }
            Action stop = () => {
                shutdown.Set();
                done.WaitOne();
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
