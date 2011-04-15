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

    class TestRegisteredTake {

        //
        // The number of producer threads.
        //

        private const int PRODUCERS = 20;

        //
        // The queue.
        //

        private static StBlockingQueue<int> queue;

        //
        // The alerter and the count down latch used for shutdown.
        //

        private static StAlerter shutdown = new StAlerter();
        private static StCountDownEvent done = new StCountDownEvent(PRODUCERS);

        //
        // The counters.
        //

        private static int[] productions = new int[PRODUCERS];
        private static int consumptions;

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
                        while (!queue.TryAdd(msg, new StCancelArgs(1, shutdown))) {
                            fails++;
                        }
                        if ((++productions[id] % 100000) == 0) {
                            VConsole.Write("-p{0}", id);
                        }
                    } catch (StThreadAlertedException) { }
                    Thread.Sleep(0);
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ p #{0} exiting: [{1}/{2}]",
                                   id, productions[id], fails);
                done.Signal();
            }
        }

        //
        // The take consumer callback
        //

        private static void TakeCallback<T>(object state, T di, bool timedOut) {
            StRegisteredTake<T> regTake = (StRegisteredTake<T>)state;

            if (timedOut) {
                Console.WriteLine("+++TIMEOUT!");
            } else {
                consumptions++;
            }
        }

        //
        // Starts the test.
        //

        internal static Action Run() {
            queue = new StUnboundedBlockingQueue<int>(true);
            // queue = new StBoundedBlockingQueue<int>(4 * 1024, true);
            // queue = new StArrayBlockingQueue<int>(4 * 1024, true);

            StRegisteredTake<int> regTake = queue.RegisterTake(TakeCallback<int>, null, 250, false); 

            for (int i = 0; i < PRODUCERS; i++) {
                new Producer().Start(i, "p #" + i);
            }

            Action stop = () => {
                shutdown.Set();
                done.Wait();
                long ps = 0;
                for (int i = 0; i < PRODUCERS; i++) {
                    ps += productions[i];
                }
                VConsole.WriteLine("+++ Total: prods = {0}, cons = {1}", ps, consumptions);
                //regTake.Unregister();
            };
            return stop;
        }
    }
}

