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

#pragma warning disable 0420

namespace SlimThreading {

    //
    // The enumerate type used to defines the two possible types of wait.
    //

    internal enum WaitType { WaitAll, WaitAny };

    //
    // The wait block used with waitables, locks and condition variables.
    //

    internal sealed class WaitBlock {

        //
        // A wait block used as sentinel to several purposes.
        //

        internal static readonly WaitBlock SENTINEL = new WaitBlock();

        //
        // The link field used by the wait queues.
        //

        internal volatile WaitBlock next;

        //
        // The associated parker.
        //

        internal readonly StParker parker;

        //
        // The type of wait.
        //

        internal readonly WaitType waitType;

        //
        // The *request* field is enconded as follows:
        // - bit 31 - set one the request is a locked request.
        // - bit 30 - set when the request is a special request.
        // - bits 29, 0 - request type.
        //

        internal const int LOCKED_REQUEST  = (1 << 31);
        internal const int SPECIAL_REQUEST = (1 << 30);
        internal const int MAX_REQUEST = (SPECIAL_REQUEST - 1);
        internal volatile int request;

        //
        // The wait status specified when the owner thread of the wait
        // block is unparked.
        //

        internal readonly int waitKey;

        //
        // Constructor used with sentinel wait blocks.
        //

        internal WaitBlock() {
            waitType = WaitType.WaitAny;
            request = 0x13081953;
            waitKey = StParkStatus.Success;
        }

        //
        // Full specified constructor.
        //


        internal WaitBlock(StParker pk, WaitType t, int r, int k) {
            parker = pk;
            waitType = t;
            request = r;
            waitKey = k;
        }

        //
        // Convenience constructors.
        //

        internal WaitBlock(WaitType t, int r, int k) {
            parker = new StParker();
            waitType = t;
            request = r;
            waitKey = k;
        }

        internal WaitBlock(WaitType t, int r) {
            parker = new StParker();
            waitType = t;
            request = r;
        }

        internal WaitBlock(WaitType t) {
            parker = new StParker();
            waitType = t;
        }

        internal WaitBlock(int r) {
            parker = new StParker();
            request = r;
        }

        internal WaitBlock(StParker pk, int r) {
            parker = pk;
            request = r;
        }

        //
        // CASes on the *next* field.
        //

        internal bool CasNext(WaitBlock n, WaitBlock nn) {
            return (next == n && Interlocked.CompareExchange<WaitBlock>(ref next, nn, n) == n);
        }
    }

    //
    // A non-thread-safe queue of wait blocks.
    //

    internal struct WaitBlockQueue {

        //
        // The first and last wait blocks.
        //

        internal WaitBlock head;
        private WaitBlock tail;

        //
        // Clears the queue.
        //

        internal void Clear() {
            head = tail = null;
        }

        //
        // Enqueues a wait block at the tail of the queue.
        //

        internal void Enqueue(WaitBlock wb) {
            if (head == null) {
                head = wb;
            } else {
                tail.next = wb;
            }
            tail = wb;
        }

        //
        // Enqueues a wait block at head of the queue.
        //

        internal void EnqueueHead(WaitBlock wb) {
            if ((wb.next = head) == null) {
                tail = wb;
            }
            head = wb;
        }

        //
        // Dequeues a wait node from a non-empty queue.
        //

        internal WaitBlock Dequeue() {
            WaitBlock wb = head;
            if ((head = wb.next) == null) {
                tail = null;
            }
            wb.next = wb;       // Mark the wait block as unlinked.
            return wb;
        }

        //
        // Returns true if the queue is empty.
        //

        internal bool IsEmpty { get { return head == null; } }

        //
        // Removes the specified wait node from the queue.
        //

        internal void Remove(WaitBlock wb) {

            //
            // If the wait block was already unlinked, return.
            //

            if (wb.next == wb) {
                return;
            }

            //
            // Compute the previous wait block and perform the removal.
            //

            WaitBlock p = head;
            WaitBlock pv = null;
            while (p != null) {
                if (p == wb) {
                    if (pv == null) {
                        if ((head = wb.next) == null) {
                            tail = null;
                        }
                    } else {
                        if ((pv.next = wb.next) == null)
                            tail = pv;
                    }
                    wb.next = wb;
                    return;
                }
                pv = p;
                p = p.next;
            }
            throw new InvalidOperationException();
        }
    }

    //
    // A queue of wait blocks that allows non-blocking enqueue
    // and lock-protected dequeue.
    //

    internal struct LockedWaitQueue {

        //
        // The head and tail of the queue.
        //

        internal volatile WaitBlock head;
        internal volatile WaitBlock tail;

        //
        // The queue lock's state. This lock has no wait queue because
        // it is always acquired with TryEnter.
        //

        private const int FREE = 0;
        private const int BUSY = 1;
        private volatile int qlock;

        //
        // Initializes the queue.
        //

        internal void Init() {
            head = tail = new WaitBlock();
        }

        //
        // Advances the queue's head.
        //

        private bool AdvanceHead(WaitBlock h, WaitBlock nh) {
            if (head == h && Interlocked.CompareExchange<WaitBlock>(ref head, nh, h) == h) {
                h.next = h;     // Mark the previous head's wait block as unlinked.
                return true;
            }
            return false;
        }

        //
        // Advances the queue's tail.
        //

        private bool AdvanceTail(WaitBlock t, WaitBlock nt) {
            return (tail == t && Interlocked.CompareExchange<WaitBlock>(ref tail, nt, t) == t);
        }

        //
        // Enqueues the specified wait block and returns its
        // predecessor.
        //

        internal bool Enqueue(WaitBlock wb) {
            do {
                WaitBlock t = tail;
                WaitBlock tn = t.next;

                //
                // Do the necessary consistency checks.
                //

                if (t != tail) {
                    continue;
                }
                if (tn != null) {
                    AdvanceTail(t, tn);
                    continue;
                }

                //
                // Queue in quiescent state, try to insert the wait block.
                //

                if (t.CasNext(null, wb)) {

                    //
                    // Enqueue succeed; So, try to swing tail to the inserted
                    // wait block and return.
                    //

                    AdvanceTail(t, wb);
                    return (t == head);
                }
            } while (true);
        }

        //
        // If the queue is not locked, returns the wait block that
        // is at the front of the queue; otherwise, returns always null.
        //

        internal WaitBlock First {
            get { return (qlock == FREE) ? head.next : null; }
        }

        //
        // Returns true if the waiting queue seems empty.
        //

        internal bool IsEmpty { get { return head.next == null; } }

        //
        // Tries to lock the queue if it is free.
        //

        internal bool TryLock() {
            return (qlock == FREE && Interlocked.CompareExchange(ref qlock, BUSY, FREE) == FREE);
        }

        //
        // Sets the new head and unlocks the queue.
        //

        internal void SetHeadAndUnlock(WaitBlock nh) {

            //
            // First, remove the cancelled wait blocks that follow the
            // new queue's head.
            //

            do {
                WaitBlock w;
                if ((w = nh.next) == null || !w.parker.IsLocked || w.request < 0) {
                    break;
                }
                nh.next = nh;   // Mark old head's wait block as unlinked.
                nh = w;
            } while (true);

            //
            // Set the new head and release the queue lock, making
            // the lock and queue changes visible to all processors.
            //

            head = nh;
            Interlocked.Exchange(ref qlock, FREE);
        }

        //
        // Unlinks the specified wait block.
        //

        internal void Unlink(WaitBlock wb) {
            if (wb.next == wb || wb == head) {
                return;
            }

            //
            // Remove the cancelled wait nodes from *head* till *wb*.
            //

            WaitBlock n;
            WaitBlock pv = head;
            while ((n = pv.next) != wb) {
                if (n.parker.IsLocked) {
                    pv.next = n.next;
                    n.next = n;
                } else {
                    pv = n;
                }
            }

            //
            // Remove the wait block *wb* and also the cancelled wait
            // blocks that follow it.
            //

            do {
                pv.next = n.next;
                n.next = n;
            } while ((n = pv.next).next != null && n.parker.IsLocked);
        }
    }
}
