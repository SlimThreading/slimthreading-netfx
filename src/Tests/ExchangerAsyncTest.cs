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
    public class ExchangerAsyncTest {
        private const int DURATION = 10000;

        private const int EXCHANGERS = 19;

        private static readonly StExchanger<int> xchg = new StExchanger<int>(100);

        private static readonly StCountDownEvent done = new StCountDownEvent(EXCHANGERS);
        
        private static readonly long[][] counters = new long[EXCHANGERS][];

        private static volatile int stop;

        private static void Exchange(int id) {
            counters[id] = new long[EXCHANGERS];
            VConsole.WriteLine("+++ x #{0} started...", id);

            var state = new object();
            int fail = 0;

            ExchangeCallback<int> callback = null;
            callback = (s, yourId, timedOut) => {
                Assert.AreEqual(state, s);

                if (timedOut) {
                    fail++;
                } else {
                    counters[id][yourId]++;
                }

                if (stop == 1) {
                    VConsole.WriteLine("+++ x #{0} exiting: [{1}/{2}]", id, counters[id].Sum(), fail);
                    done.Signal();
                    return;
                }

                xchg.RegisterExchange(id, callback, state, 1);
            };

            xchg.RegisterExchange(id, callback, state, 1);
        }

        public static void Run() {
            for (int i = 0; i < EXCHANGERS; ++i) {
                Exchange(i);
            }

            Thread.Sleep(DURATION);

            stop = 1;
            done.Wait();
            long xs = 0;

            for (int i = 0; i < EXCHANGERS; ++i) {
                Assert.AreEqual(0, counters[i][i]);
                for (int j = i + 1; j < EXCHANGERS; ++j) {
                    Assert.AreEqual(counters[i][j], counters[j][i]);
                    xs += counters[i][j];
                }
            }

            Assert.IsNull(xchg.xchgPoint);

            VConsole.WriteLine("---Total unique exchanges: {0}", xs);
        }
    }
}