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
    class TestStreamBlockingQueue {

        //
        // The number of threads.
        //

        private const int PRODUCERS = 10;
        private const int CONSUMERS = 5;

        //
        // The stream blocking queue
        //

        static StStreamBlockingQueue<char> queue = new StStreamBlockingQueue<char>(1024);

        //
        // The alerter and the count down latch used for shutdown.
        //

        private static StAlerter shutdown = new StAlerter();
        private static StCountDownEvent done = new StCountDownEvent(PRODUCERS + CONSUMERS);

        //
        // The counters
        //

        static private int[] productions = new int[PRODUCERS];
        static private int[] consumptions = new int[CONSUMERS];

        //
        // The producer thread
        //

        class ProducerThread {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                int fails = 0;
                int count = 0;
                VConsole.WriteLine("+++ p #{0} started...", id);
                do {
                    string message = String.Format("-message {0} from producer {1} thread\n",
                                                   ++count, id);
                    try {
                        char[] chars = message.ToCharArray();
                        int written = queue.Write(chars, 0, chars.Length, new StCancelArgs(shutdown));
                        if (written == 0) {
                            fails++;
                        } else {
                            productions[id] += written;
                            if ((++count % 50000) == 0) {
                                VConsole.Write("-p{0}", id);
                            }
                        }
                    } catch (StThreadAlertedException) {
                        break;
                    }
                } while (!shutdown.IsSet);

                VConsole.WriteLine("+++ p #{0} exiting: [{1}/{2}]",
                                   id, productions[id], fails);
                done.Signal();
            }
        }

        //
        // The consumer thread.
        //

        class ConsumerThread {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                int fails = 0;
                char[] buffer = new char[16];
                int read;
                int count = 0;

                VConsole.WriteLine("+++ c #{0} started...", id);
                do {
                    try {
                        read = queue.Read(buffer, 0, buffer.Length,
                                          new StCancelArgs(1, shutdown));
                        if (read == 0) {
                            fails++;
                        } else {
                            consumptions[id] += read;
                            if ((++count % 50000) == 0) {
                                VConsole.Write("-c{0}", id);
                            }
                        }
                    } catch (StThreadAlertedException) {
                        while ((read = queue.Read(buffer, 0, buffer.Length,
                                                  new StCancelArgs(1))) != 0) {
                            consumptions[id] += read;
                        }
                        break;
                    }
                } while (true);
                VConsole.WriteLine("+++ c #{0} exiting : [{1}/{2}]",
                                    id, consumptions[id], fails);
                done.Signal();
            }
        }
        //
        // Starts the test.
        //

        internal static Action Run() {
            for (int i = 0; i < CONSUMERS; i++) {
                new ConsumerThread().Start(i, "c #" + i);
            }
            for (int i = 0; i < PRODUCERS; i++) {
                new ProducerThread().Start(i, "p #" + i);
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
                VConsole.WriteLine("+++ Total:  prods = {0}, cons: {1}", ps, cs);
            };
            return stop;
        }
    }
}
