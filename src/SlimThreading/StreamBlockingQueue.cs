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
    // This class implements a generic stream blocking queue .
    //

    public class StStreamBlockingQueue<T> {

        //
        // The wait node used by threads blocked on the stream
        // blocking queue.
        //

        private sealed class WaitNode<Q> : StParker {
            internal volatile WaitNode<Q> next;
            internal Q[] buffer;
            internal int offset;
            internal int remaining;

            //
            // The constructor.
            //

            internal WaitNode(Q[] buf, int off, int r) {
                buffer = buf;
                offset = off;
                remaining = r;
            }
        }

        //
        // A fifo queue of wait nodes.
        //

        private struct WaitNodeQueue<Q> {

            //
            // The first and last wait nodes.
            //

            internal WaitNode<Q> head;
            internal WaitNode<Q> tail;

            //
            // Enqueues a wait node at the tail of the queue.
            //

            internal void Enqueue(WaitNode<Q> wn) {
                if (head == null) {
                    head = wn;
                } else {
                    tail.next = wn;
                }
                tail = wn;
            }

            //
            // Dequeues a wait node from a non-empty queue.
            //

            internal WaitNode<Q> Dequeue() {
                WaitNode<Q> n = head;
                if ((head = n.next) == null) {
                    tail = null;
                }
                n.next = n;     // Mark the wait node as unlinked.
                return n;
            }

            //
            // Returns true if the queue is empty.
            //

            internal bool IsEmpty { get { return head == null; } }

            //
            // Removes the specified wait node from the queue.
            //

            internal void Remove(WaitNode<Q> wn) {

                //
                // If the wait node was already removed, return.
                //

                if (wn.next == wn) {
                    return;
                }

                //
                // Compute the previous wait node and perform the remove.
                //

                WaitNode<Q> p = head;
                WaitNode<Q> pv = null;
                while (p != null) {
                    if (p == wn) {
                        if (pv == null) {
                            if ((head = wn.next) == null) {
                                tail = null;
                            }
                        } else {
                            if ((pv.next = wn.next) == null)
                                tail = pv;
                        }
                        return;
                    }
                    pv = p;
                    p = p.next;
                }
                throw new InvalidOperationException("wait node not found!");
            }

            /*++
             * 
             * The following methods are used when the queue is
             * used as a wake up list that holds locked wait nodes.
             *
             --*/

            //
            // Adds the wait node at the tail of the queue.
            //

            internal void Add(WaitNode<Q> wn) {
                wn.next = null;
                if (head == null) {
                    head = wn;
                } else {
                    tail.next = wn;
                }
                tail = wn;
            }

            //
            // Unparks all the wait nodes in the queue.
            //

            internal void UnparkAll() {
                WaitNode<Q> p = head;
                while (p != null) {
                    p.Unpark(StParkStatus.Success);
                    p = p.next;
                }
            }
        }

        //
        // Constants.
        //

        private const int LONG_CRITICAL_SECTION_SPINS = 150;

        //
        // The queue's lock.
        //

        private SpinLock slock;

        //
        // The wait queue
        //

        private WaitNodeQueue<T> waitQueue;

        //
        // The items buffer, its length, the number of available items
        // and the head and tail indexs.
        //

        private T[] items;
        private readonly int length;
        private int available;
        private int head;
        private int tail;

        //
        // Constructors.
        //

        public StStreamBlockingQueue(int capacity) {
            if (capacity <= 0) {
                throw new ArgumentOutOfRangeException("capacity");
            }
            items = new T[length = capacity];
            waitQueue = new WaitNodeQueue<T>();
            slock = new SpinLock(LONG_CRITICAL_SECTION_SPINS);
        }

        //
        // Writes the specified number of data items to the
        // stream blocking queue, activating the specified cancellers.
        //

        public int Write(T[] buffer, int offset, int count, StCancelArgs cargs) {

            //
            // If this is a zero length write, return immediately.
            //

            if (count == 0) {
                return 0;
            }

            //
            // Initialize the local variables.
            //

            int remaining = count;
            WaitNodeQueue<T> wl = new WaitNodeQueue<T>();
            int toCopy;

            //
            // Acquire the queue's lock.
            //

            slock.Enter();

            //
            // If the queue's buffer is full, check if the current
            // thread must wait.
            //

            if (available == length) {
                goto CheckForWait;
            }

            //
            // If there are waiting readers (i.e., the buffer is empty),
            // transfer data items transferring directly to the waiting
            // readers' buffers.
            //

            if (!waitQueue.IsEmpty) {
                do {
                    WaitNode<T> rdw = waitQueue.head;

                    //
                    // Compute the number of data items to transfer to the
                    // waiting reader's buffer and perform the transfer.
                    //

                    if ((toCopy = rdw.remaining) > remaining) {
                        toCopy = remaining;
                    }
                    Array.Copy(buffer, offset, rdw.buffer, rdw.offset, toCopy);
                    rdw.remaining -= toCopy;
                    rdw.offset += toCopy;
                    remaining -= toCopy;
                    offset += toCopy;

                    //
                    // If the waiting reader completes its read operation,
                    // remove its wait node from the wait list and try to
                    // lock the associated parker.
                    //

                    if (rdw.remaining == 0) {
                        waitQueue.Dequeue();
                        if (rdw.TryLock() && !rdw.UnparkInProgress(StParkStatus.Success)) {
                            wl.Add(rdw);
                        }
                    } else {

                        //
                        // The data items are not enough to satisfy the waiting
                        // reader that is at front of wait queue, so break the loop.
                        //

                        break;
                    }
                } while (remaining != 0 && !waitQueue.IsEmpty);
            }

            //
            // If we have still data items to write and there is free space
            // in the queue's buffer, transfer the appropriate number of data
            // items the queue's buffer.
            //

            if (remaining != 0 && available < length) {

                //
                // Compute the number of data items that can be copied
                // to the queue's buffer and perform the transfer.
                //

                if ((toCopy = remaining) > (length - available)) {
                    toCopy = length - available;
                }
                int t = tail;
                int tillEnd;
                if ((tillEnd = length - t) >= toCopy) {
                    Array.Copy(buffer, offset, items, t, toCopy);
                    if ((t += toCopy) >= length) {
                        t = 0;
                    }
                } else {
                    int fromBegin = toCopy - tillEnd;
                    Array.Copy(buffer, offset, items, t, tillEnd);
                    Array.Copy(buffer, offset + tillEnd, items, 0, fromBegin);
                    t = fromBegin;
                }

                //
                // Update counters and indexes.
                //

                tail = t;
                available += toCopy;
                remaining -= toCopy;
                offset += toCopy;
            }

        CheckForWait:

            //
            // If there are still data items to write, the current thread must
            // wait if a null timeout wasn't specified.
            //

            WaitNode<T> wn = null;
            bool mustWait;
            if (mustWait = (remaining != 0 && cargs.Timeout != 0)) {
                waitQueue.Enqueue(wn = new WaitNode<T>(buffer, offset, remaining));
            }

            //
            // Release the queue's lock and unpark the readers threads
            // release above.
            //
            
            slock.Exit();
            wl.UnparkAll();

            //
            // If the current thread doesn't need to wait, return the
            // number of written data items.
            //

            if (!mustWait) {
                return (count - remaining);
            }

            //
            // Park the current thread, activating the specified cancellers.
            //

            int ws = wn.Park(cargs);

            //
            // If the write was completed, return the number of written
            // data items.
            //

            if (ws == StParkStatus.Success) {
                return count;
            }

            //
            // The write was cancelled due to timeout, alert or interrupt.
            // If the wait node is still inserted in the wait queue, acquire
            // the queue's lock and unlink it.
            //

            if (wn.next != wn) {
                slock.Enter();
                waitQueue.Remove(wn);
                slock.Exit();
            }

            //
            // If at least a data item was written, ignore the cancellation
            // and return the number of transferred data items.
            //

            int c;
            if ((c = count - wn.remaining) != 0) {
                StCancelArgs.PostponeCancellation(ws);
                return c;
            }

            //
            // No data items were written; so, report the failure appropriately.
            //

            StCancelArgs.ThrowIfException(ws);
            return 0;
        }

        //
        // Waits unconditionally until write the specified number of
        // data items to the stream blocking queue.
        //

        public int Write(T[] buffer, int offset, int count) {
            return Write(buffer, offset, count, StCancelArgs.None);
        }


        //
        // Waits until read the specified number of data items from
        // the stream queue, activating the specified cancellers.
        //

        public int Read(T[] buffer, int offset, int count, StCancelArgs cargs) {

            //
            // If this is a zero length read, return immediately.
            //

            if (count == 0) {
                return 0;
            }

            //
            // Initialize the local variables and acquire the queue's lock.
            //

            int remaining = count;
            WaitNodeQueue<T> wl = new WaitNodeQueue<T>();
            int toCopy;
            slock.Enter();

            //
            // If the queue's buffer is empty, check if the current thread
            // must wait.
            //

            if (available == 0) {
                goto CheckForWait;
            }
 
            //
            // Compute the number of data items that we can read
            // from the queue's buffer and perform the transfer.
            //

            if ((toCopy = remaining) > available) {
                toCopy = available;
            }
            int h = head;
            int tillEnd;
            if ((tillEnd = length - h) >= toCopy) {
                Array.Copy(items, h, buffer, offset, toCopy);
                if ((h += toCopy) >= length) {
                    h = 0;
                }
            } else {
                int fromBegin = toCopy - tillEnd;
                Array.Copy(items, h, buffer, offset, tillEnd);
                Array.Copy(items, 0, buffer, offset + tillEnd, fromBegin);
                h = fromBegin;
            }

            //
            // Adjust counters and indexes.
            //

            head = h;
            available -= toCopy;
            remaining -= toCopy;
            offset += toCopy;

            //
            // If we have still data items to read and there are waiting
            // writers, transfer the data directly from our buffer to the
            // waiting writers' buffers.
            //

            if (remaining != 0 && !waitQueue.IsEmpty) {
                do {
                    WaitNode<T> wrw = waitQueue.head;

                    //
                    // Compute the number of data items to transfer to the
                    // waiting writer's buffer and perform the transfer.
                    //

                    if ((toCopy = remaining) > wrw.remaining) {
                        toCopy = wrw.remaining;
                    }
                    Array.Copy(wrw.buffer, wrw.offset, buffer, offset, toCopy);
                    wrw.remaining -= toCopy;
                    wrw.offset += toCopy;
                    remaining -= toCopy;
                    offset += toCopy;

                    //
                    // If the write operation was completed, try to release
                    // the writer thread.
                    //

                    if (wrw.remaining == 0) {
                        waitQueue.Dequeue();
                        if (wrw.TryLock() && !wrw.UnparkInProgress(StParkStatus.Success)) {
                            wl.Add(wrw);
                        }
                    } else {

                        //
                        // The read is completed, so break the loop.
                        //

                        break;
                    }
                } while (remaining != 0 && !waitQueue.IsEmpty);
            }

            //
            // If there is available space in the queue's buffer and
            // waiting writers, try to fill the queue's buffer with
            // the data of the waiting writers.
            //

            if (available < length && !waitQueue.IsEmpty) {
                do {
                    WaitNode<T> wrw = waitQueue.head;

                    //
                    // Compute the number of data items to copy from the writer's
                    // buffer to the queue's buffer and perform the transfer.
                    //

                    if ((toCopy = wrw.remaining) > (length - available)) {
                        toCopy = length - available;
                    }

                    int t = tail;
                    if ((tillEnd = length - t) >= toCopy) {
                        Array.Copy(wrw.buffer, wrw.offset, items, t, toCopy);
                        if ((t += toCopy) >= length) {
                            t = 0;
                        }
                    } else {
                        int fromBegin = toCopy - tillEnd;
                        Array.Copy(wrw.buffer, wrw.offset, items, t, tillEnd);
                        Array.Copy(wrw.buffer, wrw.offset + tillEnd, items, 0, fromBegin);
                        t = fromBegin;
                    }

                    //
                    // Update counters and indexes.
                    //

                    tail = t;
                    available += toCopy;
                    wrw.remaining -= toCopy;
                    wrw.offset += toCopy;

                    //
                    // If the writer completed its write, release it.
                    //

                    if (wrw.remaining == 0) {
                        waitQueue.Dequeue();
                        if (wrw.TryLock() && !wrw.UnparkInProgress(StParkStatus.Success)) {
                            wl.Add(wrw);
                        }
                    } else {

                        //
                        // The queue's buffer is full, so break the loop.
                        //

                        break;
                    }
                } while (available < length && !waitQueue.IsEmpty);
            }

        CheckForWait:

            //
            // If the read operation was not completed, the current thread
            // must wait if it didn't read all data items and a null timeout
            // wasn't specified.
            //

            WaitNode<T> wn = null;
            bool mustWait;
            if (mustWait = (remaining != 0 && cargs.Timeout != 0)) {
                waitQueue.Enqueue(wn = new WaitNode<T>(buffer, offset, remaining));
            }

            //
            // Release the queue's lock and unpark the released waiters.
            //

            slock.Exit();
            wl.UnparkAll();

            //
            // If the read was completed or the thread specified a null
            // timeout, return the number of read data items.
            //

            if (!mustWait) {
                return (count - remaining);
            }

            //
            // Park the current thread, activating the specified cancellers.
            //

            int ws = wn.Park(cargs);

            //
            // If succeed, return the number of data items transferred.
            //

            if (ws == StParkStatus.Success) {
                return count;
            }

            //
            // The read was cancelled due to timeout, alert or interrupt.
            // If the wait block is still inserted in the wait queue, acquire
            // the queue's lock and remove it from the queue.
            //

            if (wn.next != wn) {
                slock.Enter();
                waitQueue.Remove(wn);
                slock.Exit();
            }
            int c;
            if ((c = count - wn.remaining) != 0) {
                StCancelArgs.PostponeCancellation(ws);
                return c;
            }

            //
            // No data items were transferred, so report the failure
            // appropriately.
            //

            StCancelArgs.ThrowIfException(ws);
            return 0;
        }
    }
}
