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

    class TestInitOnceLock {

        //
        // The number of threads.
        //

        private const int THREADS = 10;

        //
        // The init once lock and the target.
        //

        private static StInitOnceLock initLock = new StInitOnceLock();
        private static StSemaphore lazySem;

        //
        // The count down event used to synchronize cycle shutdown.
        //

        private static StCountDownEvent cycleDone;

        //
        // The alerter and the event used to synchronize test shutdown.
        //

        private static StAlerter shutdown = new StAlerter();
        private static StNotificationEvent done = new StNotificationEvent();

        //
        // The probability at which the target creation succeeds.
        //

        private const int P = 25;
        private static Random rnd = new Random(Environment.TickCount);

        //
        // The target factory method.
        //

        private static StSemaphore NewSemaphore() {
            if (rnd.Next(100) < P) {
                StSemaphore s = new StSemaphore(THREADS, THREADS);
                return s;
            } else {
                throw new InvalidOperationException("create failed");
            }
        }

        //
        // Initializer thread
        //

        class InitializerThread {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                Thread.Sleep(new Random(id).Next(1000));
                VConsole.WriteLine("+++ i #{0} started...", id);
                do {
                    try {
                        StLazyInitializer.EnsureInitialized<StSemaphore>(ref lazySem, ref initLock,
                                                                       NewSemaphore);

                        //StLazyInitializer.EnsureInitialized<Semaphore>(ref lazySem, NewSemaphore);

                        //
                        // Wait on the initialized semaphore.
                        //

                        lazySem.WaitOne();
                        break;
                    } catch (Exception ex) {
                        VConsole.WriteLine("*** i #{0} exception: {1}", id, ex.Message);
                    }
                } while (true);
                VConsole.WriteLine("+++ i #{0} exits...", id);
                cycleDone.Signal();
            }
        }

        //
        // The controller thread.
        //

        class Controller {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                do {
                    VConsole.WriteLine("----------------");
                    initLock = new StInitOnceLock();
                    cycleDone = new StCountDownEvent(THREADS);
                    for (int i = 0; i < THREADS; i++) {
                        new InitializerThread().Start(i, "i #" + i);
                    }
                    cycleDone.Wait();
                    lazySem = null;
                    //Thread.Sleep(1000);
                } while (!shutdown.IsSet);
                done.Set();
            }
        }

        //
        // Starts the test.
        //

        internal static Action Run() {
            new Controller().Start(0, "c #0");
            Action stop = () => {
                shutdown.Set();
                done.Wait();
            };
            return stop;
        }
    }
}
