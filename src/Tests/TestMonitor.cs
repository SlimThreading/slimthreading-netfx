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
using System.Collections.Generic;
using System.Threading;
using SlimThreading;

namespace TestShared {

	class MonitorBasedSemaphore {
		private int permits;
		private readonly StReentrantLock m;
		private readonly StConditionVariable cv;

		public MonitorBasedSemaphore(int initial) {
			m = new StReentrantLock();
			cv = new StConditionVariable(m);
			if (initial > 0)
				permits = initial;
		}

		public bool Acquire(int id, StCancelArgs cargs) {
			m.Enter();
			try {
				if (permits >= id) {
					permits -= id;
					return true;
				}
				while (true) {
					int lastTime = Environment.TickCount;
					cv.Wait(cargs);
					if (permits >= id) {
						permits -= id;
						return true;
					}
                    if (!cargs.AdjustTimeout(ref lastTime)) {
                        return false;
                    }
				}
			} finally {
				m.Exit();
			}
		}

		public void Acquire(int id) { Acquire(id, StCancelArgs.None); }

		public void Release(int id) {
			m.Enter();
			try {
				permits += id;
				cv.PulseAll();
			} finally {
				m.Exit();
			}
		}

		public void Release() { Release(1); }

		public override string ToString() {
			return base.ToString() + "[Permits = " + permits + "]";
		}
	}

    class MonitorBasedLinkedBlockingQueue<T> {
		private readonly int capacity;
		private readonly Queue<T> buffer;
		private readonly StReentrantLock m;
		private readonly StConditionVariable cv;

		public MonitorBasedLinkedBlockingQueue(int capacity) {
			if (capacity <= 0)
				throw new ArgumentException("capacity");
			this.capacity = capacity;
			buffer = new Queue<T>(capacity);
			m = new StReentrantLock();
			cv = new StConditionVariable(m);
		}

		public bool Offer(T data, StCancelArgs cargs) {
			m.Enter();
			try {
				if (buffer.Count < capacity) {
					buffer.Enqueue(data);
					cv.PulseAll();
					return true;
				}
				while (true) {
					int lastTime = Environment.TickCount;
					cv.Wait(cargs);

					if (buffer.Count < capacity) {
						buffer.Enqueue(data);
						cv.PulseAll();
						return true;
					}
                    if (!cargs.AdjustTimeout(ref lastTime)) {
                        return false;
                    }
				}
			} finally {
				m.Exit();
			}
		}

		public void Put(T data) {
            Offer(data, StCancelArgs.None);
        }

		public bool Poll(out T di, StCancelArgs cargs) {
			m.Enter();
			try {
				if (buffer.Count > 0) {
					di = buffer.Dequeue();
					cv.PulseAll();
					return true;
				}
				while (true) {
					int lastTime = Environment.TickCount;
					cv.Wait(cargs);
					if (buffer.Count > 0) {
						di = buffer.Dequeue();
						cv.PulseAll();
						return true;
					}
                    if (!cargs.AdjustTimeout(ref lastTime)) {
                        di = default(T);
                        return false;
                    }
				}
			} finally {
				m.Exit();
			}
		}

		public bool Take(out T di) {
            return Poll(out di, StCancelArgs.None); }

		public override string ToString() {
			return base.ToString() + "[Count = " + buffer.Count + "]";
		}
	}

	class MonitorBasedLinkedBlockingQueue2<T> {
		private readonly int capacity;
		private readonly Queue<T> buffer;
		//private readonly StReentrantLock m = new StReentrantLock();
        //private readonly StFairLock m = new StFairLock();
        private readonly StReentrantFairLock m = new StReentrantFairLock();
        private readonly StConditionVariable nonFull;
		private readonly StConditionVariable nonEmpty;

		public MonitorBasedLinkedBlockingQueue2(int capacity) {
			if (capacity <= 0)
				throw new ArgumentException("capacity");
			this.capacity = capacity;
			buffer = new Queue<T>(capacity);
			nonFull = new StConditionVariable(m);
			nonEmpty = new StConditionVariable(m);
		}

		public bool Offer(T data, StCancelArgs cargs) {
			m.WaitOne();
			try {
				if (buffer.Count < capacity) {
					buffer.Enqueue(data);
					nonEmpty.Pulse();
					return true;
				}
				while (true) {
					int lastTime = Environment.TickCount;
					nonFull.Wait(cargs);

					if (buffer.Count < capacity) {
						buffer.Enqueue(data);
						nonEmpty.Pulse();
						return true;
					}
                    if (!cargs.AdjustTimeout(ref lastTime)) {
                        return false;
                    }
				}
			} finally {
				m.Exit();
			}
		}

		public void Put(T data) {
            Offer(data, StCancelArgs.None);
        }

		public bool Poll(out T di, StCancelArgs cargs) {
			m.WaitOne();
			try {
				if (buffer.Count > 0) {
					di = buffer.Dequeue();
					nonFull.Pulse();
					return true;
				}
				while (true) {
					int lastTime = Environment.TickCount;
					nonEmpty.Wait(cargs);
					if (buffer.Count > 0) {
						di = buffer.Dequeue();
						nonFull.Pulse();
						return true;
					}
                    if (!cargs.AdjustTimeout(ref lastTime)) {
                        di = default(T);
                        return false;
                    }
				}
			} finally {
				m.Exit();
			}
		}

		public void Take(out T di) {
            Poll(out di, StCancelArgs.None);
        }

		public override string ToString() {
			return base.ToString() + "[Count = " + buffer.Count + "]";
		}
	}

    //
    // Test a monitor based semaphore.
    //

	class TestMonitorBasedSemaphore {

        //
        // The number of threads.
        //

		private const int RELEASERS = 1;
		private const int ACQUIRERS = 5;

        //
        // The monitor based semaphore.
        //

		private static MonitorBasedSemaphore sem = new MonitorBasedSemaphore(0);

        //
        // The alerter and the count down event used for shutdown.
        //

        private static StAlerter shutdown = new StAlerter();
        private static StCountDownEvent done = new StCountDownEvent(ACQUIRERS + RELEASERS);

        //
        // The counters.
        //

		static int[] acquires = new int[ACQUIRERS];
		static int[] releases = new int[RELEASERS];

        //
        // The releaser thread.
        //

        class Releaser {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {

                VConsole.WriteLine("+++ r #{0} started...", id);
                do {
                    sem.Release(1);
                    if ((++releases[id] % 10000) == 0) {
                        VConsole.Write("-r{0}", id);
                    }
                    // Thread.Sleep(0);
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ r #{0} exiting: [{1}]", id, releases[id]);
                done.Signal();
            }
        }

        //
        // The acquirer thread.
        //

        class Acquirer {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                int fail = 0;

                VConsole.WriteLine("+++ a #{0} started...", id);
                do {
                    try {
                        if (sem.Acquire(1, new StCancelArgs(1, shutdown))) {
                            if ((++acquires[id] % 10000) == 0) {
                                VConsole.Write("-a{0}", id);
                            }
                        } else {
                            fail++;
                        }
                    } catch (StThreadAlertedException) {
                        break;
                    }
                } while (true);
                VConsole.WriteLine("+++ a #{0} exiting: [{1}/{2}]",
                                  id, acquires[id], fail);
                done.Signal();
            }
        }

        //
        // Starts the test.
        //

        internal static Action Run() {
            for (int i = 0; i < ACQUIRERS; i++) {
                new Acquirer().Start(i, "a #" + i);
            }
            for (int i = 0; i < RELEASERS; i++) {
                new Releaser().Start(i, "p #" + i);
            }
            Action stop = () => {
                shutdown.Set();
                done.WaitOne();
                long rels = 0, acqs = 0;
                for (int i = 0; i < RELEASERS; i++) {
                    rels += releases[i];
                }
                for (int i = 0; i < ACQUIRERS; i++) {
                    acqs += acquires[i];
                }

                VConsole.WriteLine("+++ Total: rels = {0}, acqs = {1}", rels, acqs);
            };
            return stop;
        }
	}

    //
    // Test a monitor based blocking queue.
    //

	class TestMonitorBasedLinkedBlockingQueue {

        //
        // The number of threads.
        //

		private const int PRODUCERS = 10;
		private const int CONSUMERS = 20;

        //
        // The blocking queue.
        //

		private static MonitorBasedLinkedBlockingQueue<String> q = new MonitorBasedLinkedBlockingQueue<string>(1000);
        //private static MonitorBasedLinkedBlockingQueue2<String> q = new MonitorBasedLinkedBlockingQueue2<string>(1000);
	
        //
        // The alerter and the count down event used for shutdown.
        //

        private static StAlerter shutdown = new StAlerter();
        private static StCountDownEvent done = new StCountDownEvent(PRODUCERS + CONSUMERS);

        //
        // The counters.
        //
        
        private static int[] prods = new int[PRODUCERS];
        private static int[] cons = new int[CONSUMERS];
	
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
                String data = "data";
                do {
                    try {
                        q.Offer(data, new StCancelArgs(shutdown));
                        if ((++prods[id] % 10000) == 0) {
                            VConsole.Write("-p{0}", id);
                        }
                    } catch (StThreadAlertedException) { }
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ p #{0} exiting: [{1}]", id, prods[id]);
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
                Random r = new Random((id + 1) * Environment.TickCount);
                int fail = 0;
                do {
                    try {
                        string data;
                        if (q.Poll(out data, new StCancelArgs(r.Next(2), shutdown))) {
                            if ((++cons[id] % 10000) == 0) {
                                VConsole.Write("-c{0}", id);
                            }
                        } else {
                            fail++;
                        }
                    } catch (StThreadAlertedException) {
                        break;
                    }
                } while (true);
                VConsole.WriteLine("+++ c #{0} exiting: [{1}/{2}]", id, cons[id], fail);
                done.Signal();
            }
        }

        //
        // Starts the test.
        //

        internal static Action Run() {
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
                    ps += prods[i];
                }
                for (int i = 0; i < CONSUMERS; i++) {
                    cs += cons[i];
                }
                VConsole.WriteLine("+++ Total: prods = {0}, cons = {1}", ps, cs);
            };
            return stop;
        }
	}
}
