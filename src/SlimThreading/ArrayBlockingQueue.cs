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

namespace SlimThreading {

    //
    // This class implements a array-based bounded blocking queue
    // that holds data items of type T.
    //

    public class StArrayBlockingQueue<T> : StBlockingQueue<T> {

        //
        // The queue's lock and the number of spin cycles
        // executed when the lock is busy.
        //

        private SpinLock qlock;
        private const int LOCK_SPIN_CYCLES = 100;

        //
        // The queue buffer, the buffer length, the available data items
        // and the head and tail indexs.
        //

        private readonly T[] items;
        private readonly int length;
        private volatile int count;
        private int head;
        private int tail;

        //
        // The waiting queue and its discipline.
        //

        private WaitQueue waitQueue;
        private readonly bool lifoQueue;

        //
        // Constructors.
        //

        public StArrayBlockingQueue(int capacity, bool lifoQueue) {
            if (capacity <= 0) {
                throw new ArgumentOutOfRangeException("capacity");
            }
            items = new T[length = capacity];
            qlock = new SpinLock(LOCK_SPIN_CYCLES);
            this.lifoQueue = lifoQueue;
            waitQueue = new WaitQueue();
        }

        public StArrayBlockingQueue(int capacity) : this(capacity, false) { }


        //
        // Tries to add immediatelly a data item to the queue.
        //

        public override bool TryAdd(T di) {

            //
            // If the queue is full, return failure immediatelly.
            //

            if (count == length) {
                return false;
            }

            //
            // The queue seems non-full; so, acquire the queue's lock.
            //

            qlock.Enter();

            //
            // When the queue's buffer has free slots, it can't have threads
            // blocked by the add operation; so, if there are waiters, they
            // were blocked by the take operation.
            //

            if (count < length) {
                if (!waitQueue.IsEmpty) {

                    //
                    // Try to deliver the data item directly to a waiting thread.
                    //

                    do {
                        WaitNode w = waitQueue.Dequeue();
                        StParker pk = w.parker;
                        if (pk.TryLock()) {

                            //
                            // Release the queue's lock, pass the data item through
                            // the wait node, unpark the waiter thread and return
                            // success.
                            //

                            qlock.Exit();
                            w.channel = di;
                            pk.Unpark(w.waitKey);
                            return true;
                        }
                    } while (!waitQueue.IsEmpty);
                }

                //
                // There is at least a free slot on the queue; So, copy the
                // data item to the queue's buffer, unlock the queue and
                // return success.
                //

                items[tail] = di;
                if (++tail == length) {
                    tail = 0;
                }
                count++;
                qlock.Exit();
                return true;
            }

            //
            // The queue's buffer is full, so return false.
            //

            qlock.Exit();
            return false;
        }

        //
        // Executes the prologue of the BlockingQueue<T>.TryAddXxx method.
        // 

        internal override WaitNode TryAddPrologue(StParker pk, int key, T di, ref WaitNode ignored) {

            //
            // Acquire the queue's lock.
            //

            qlock.Enter();

            //
            // ...
            //

            if (count < length) {
                if (!pk.TryLock()) {
                    qlock.Exit();
                    return null;
                }
                pk.UnparkSelf(key);

                //
                // If the wait queue isn't empty, try to deliver the data item
                // directly to a waiting thread.
                //

                if (!waitQueue.IsEmpty) {
                    do {
                        WaitNode w = waitQueue.Dequeue();
                        StParker wpk = w.parker;
                        if (wpk.TryLock()) {

                            //
                            // Release the queue's lock, pass the data item through
                            // the wait node, unpark the waiter thread and return
                            // success.
                            //

                            qlock.Exit();
                            w.channel = di;
                            wpk.Unpark(w.waitKey);
                            return null;
                        }
                    } while (!waitQueue.IsEmpty);
                }

                //
                // Add the data item to the non-full queue and return null.
                //

                items[tail] = di;
                if (++tail == length) {
                    tail = 0;
                }
                count++;
                qlock.Exit();
                return null;
            }

            //
            // The queue's buffer is full, so, ...
            //

            WaitNode wn;
            waitQueue.Enqueue(wn = new WaitNode(pk, key, di));

            //
            // Release the queue's lock and return the inserted wait block.
            //

            qlock.Exit();
            return wn;
        }

        //
        // Cancels the specified add attempt.
        //

        internal override void CancelAddAttempt(WaitNode wn, WaitNode ignored) {
            if (wn.next != wn) {
                qlock.Enter();
                waitQueue.Remove(wn);
                qlock.Exit();
            }
        }

        //
        // Tries to take immediately a data item from the queue.
        //

        public override bool  TryTake(out T di) {

	        //
	        // If the queue seems empty, return failure.
	        //

	        if (count == 0) {
                di = default(T);
                return false;
            }

	        //
	        // The queue seems non-empty; so, acquire the queue's lock.
	        //

            qlock.Enter();

	        //
	        // If the queue is now empty, release the queue's lock
            // and return failure. If it is actually empty, return failure.
	        //

	        if (count == 0) {
                qlock.Exit();
                di = default(T);
                return false;
            }

	        //
	        // Retrieve the next data item from the queue's buffer.
	        //

            di = items[head];
            if (++head == length) {
                head = 0;
            }
            count--;

	        //
	        // If the wait queue is empty, release the queue's lock and
            // return success.
	        //

	        if (waitQueue.IsEmpty) {
                qlock.Exit();
                return true;
            }

	        //
	        // There are threads blocked by the add operation. So, try to use
            // the freed buffer slot to release one of the waiter threads.
	        //

            do {
		        WaitNode w = waitQueue.Dequeue();
                StParker pk = w.parker;
                if (pk.TryLock()) {
			        items[tail] = w.channel;
                    if (++tail == length) {
                        tail = 0;
                    }
                    count++;
                    qlock.Exit();
                    pk.Unpark(w.waitKey);
                    return true;
                }
            } while (!waitQueue.IsEmpty);

	        //
	        // Release the queue's lock and return success.
	        //

            qlock.Exit();
            return true;
        }

        //
        // Executes the prologue of the BlockingQueue<T>.TryTake method.
        // 

        internal override WaitNode TryTakePrologue(StParker pk, int key, out T di, ref WaitNode ignored) {

	        //
            // Acquire the queue's lock and check if the queue's is empty.
	        //

            qlock.Enter();
	        if (count != 0) {

                //
                // The queue isn't empty; so, ...
                //

                if (!pk.TryLock()) {
                    qlock.Exit();
                    di = default(T);
                    return null;
                }
                pk.UnparkSelf(key);

                //
                // Retrieve a data item from the queue.
                //

                di = items[head];
                if (++head == length) {
                    head = 0;
                }
                count--;

                //
                // If the wait queue isn't empty, try to use the freed
                // slot to release one of the waiter threads.
                //

                if (!waitQueue.IsEmpty) {
                    do {
                        WaitNode w = waitQueue.Dequeue();
                        StParker pk2 = w.parker;
                        if (pk2.TryLock()) {
                            items[tail] = w.channel;
                            if (++tail == length) {
                                tail = 0;
                            }
                            count++;
                            qlock.Exit();
                            pk2.Unpark(w.waitKey);
                            return null;
                        }
                    } while (!waitQueue.IsEmpty);
                }

                //
		        // Release the queue's lock and return null to signal that the
		        // take operation was accomplished.
		        //

                qlock.Exit();
                return null;
            }

	        //
	        // The queue's buffer is empty; so, create ...
            //

            WaitNode wn = new WaitNode(pk, key);

            if (lifoQueue) {
                waitQueue.EnqueueHead(wn);
            } else {
                waitQueue.Enqueue(wn);
            }

	        //
	        // Release the queue's lock and return the wait node
            // inserted in the wait queue.
	        //

            qlock.Exit();
            di = default(T);
            return wn;
        }

        //
        // Cancels the specified take operation.
        //

        internal override void  CancelTakeAttempt(WaitNode wn, WaitNode ignored) {
            if (wn.next != wn) {
                qlock.Enter();
                waitQueue.Remove(wn);
                qlock.Exit();
            }
        }
    }
}
