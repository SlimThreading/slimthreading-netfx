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
    // This is the base class for all flavours of blocking queues.
    //

	public abstract class StBlockingQueue<T> {

        //
        // The wait node used with blocking queues.
        //

        internal sealed class WaitNode {
            internal volatile WaitNode next;
            internal readonly StParker parker;
            internal readonly int waitKey;
            internal T channel;

            internal WaitNode(StParker pk, int key, T data) {
                parker = pk;
                waitKey = key;
                channel = data;
            }

            internal WaitNode(StParker pk, int key) {
                parker = pk;
                waitKey = key;
            }

            internal WaitNode(int key) : this(new StParker(), key) { }

            internal WaitNode(int key, T data) : this(new StParker(), key, data) { }

            //
            // CASes the *next* field.
            //

            internal bool CasNext(WaitNode n, WaitNode nn) {
                return (next == n && Interlocked.CompareExchange<WaitNode>(ref next, nn, n) == n);
            }
        }

        //
        // The abstract base class of the non-blocking queues of wait nodes.
        //

        internal abstract class NonBlockingWaitQueue {

            //
            // Enqueues a wait node.
            //

            internal abstract WaitNode Enqueue(WaitNode wn);

            //
            // Tries to dequeue a wait node and to lock the associated
            // parker.
            //

            internal abstract WaitNode TryDequeueAndLock();

            //
            // Unlinks the specified wait node given its predecessor.
            //

            internal abstract void Unlink(WaitNode wn, WaitNode pred);

            //
            // Returns true if the queue is empty.
            //

            internal abstract bool IsEmpty { get; }
        }

        //
        // The non-blocking FIFO wait queue.
        //

        internal class NonBlockingFifoWaitQueue : NonBlockingWaitQueue {

            //
            // The head and tail of the non-blocking queue.
            //

            private volatile WaitNode head;
            private volatile WaitNode tail;

            //
            // The predecessor of a wait block that couldn't be unlinked
            // before before it was the last wait node of the queue.
            //

            private volatile WaitNode toUnlink;

            //
            // Constructor.
            //

            internal NonBlockingFifoWaitQueue() {
                head = tail = new WaitNode(null, 0, default(T));
            }

            //
            // Advances the queue's head.
            //

            private bool AdvanceHead(WaitNode h, WaitNode nh) {
                if (head == h && Interlocked.CompareExchange<WaitNode>(ref head, nh, h) == h) {
                    h.next = h;     // Forget next.
                    return true;
                }
                return false;
            }

            //
            // Advances the queue's tail.
            //

            private bool AdvanceTail(WaitNode t, WaitNode nt) {
                return (tail == t && Interlocked.CompareExchange<WaitNode>(ref tail, nt, t) == t);
            }

            //
            // CASes on the *toUnlink* field.
            //

            private bool CasToUnlink(WaitNode tu, WaitNode ntu) {
                return (toUnlink == tu &&
                        Interlocked.CompareExchange<WaitNode>(ref toUnlink, ntu, tu) == tu);
            }

            //
            // Enqueues the specified wait node.
            //

            internal override WaitNode Enqueue(WaitNode wn) {
                do {
                    WaitNode t = tail;
                    WaitNode tn = t.next;

                    //
                    // If the queue is in the intermediate state, try to advance
                    // the its tail.
                    //

                    if (tn != null) {
                        AdvanceTail(t, tn);
                        continue;
                    }

                    //
                    // Queue in quiescent state, so try to insert the new node.
                    //

                    if (t.CasNext(null, wn)) {

                        //
                        // Advance the tail and return the previous wait block.
                        //

                        AdvanceTail(t, wn);
                        return t;
                    }
                } while (true);
            }

            //
            // Tries to dequeue a wait node and to lock the associated parker.
            //

            internal override WaitNode TryDequeueAndLock() {
                do {
                    WaitNode h = head;
                    WaitNode t = tail;
                    WaitNode hn = h.next;

                    if (h == t) {
                        if (hn == null) {
                            return null;
                        }

                        //
                        // Tail is falling behind, try to advance it.
                        //

                        AdvanceTail(t, hn);
                        continue;
                    }

                    //
                    // The queue seems non-empty, so do the consistency checks and
                    // try to remove the first node.
                    //

                    if (h != head || t != tail || hn == null) {
                        continue;
                    }
                    if (AdvanceHead(h, hn)) {

                        //
                        // Try to lock the associated parker and, if succeed,
                        // return the wait block.
                        //

                        if (hn.parker.TryLock()) {
                            return hn;
                        }
                    }
                } while (true);
            }

            //
            // Returns true if the queue is empty.
            //

            internal override bool IsEmpty {
                get { return head.next == null; }
            }

            //
            // Unlinks the specified wait block from the queue.
            //

            internal override void Unlink(WaitNode wn, WaitNode pred) {
                while (pred.next == wn) {

                    //
                    // Remove all locked wait nodes that are at the front of
                    // the queue.
                    //

                    WaitNode h, hn;
                    if ((hn = (h = head).next) != null && hn.parker.IsLocked) {
                        AdvanceHead(h, hn);
                        continue;
                    }

                    //
                    // If the queue is already empty, return.
                    //

                    WaitNode t;
                    if ((t = tail) == h) {
                        return;
                    }

                    //
                    // If the queue's state changed underneath us, retry.
                    //

                    WaitNode tn = t.next;
                    if (t != tail) {
                        continue;
                    }

                    //
                    // If an insert is in progress, advance the tail and retry.
                    //

                    if (tn != null) {
                        AdvanceTail(t, tn);
                        continue;
                    }

                    //
                    // If the wait node is not at the tail of the queue, try
                    // to unlink it.
                    //

                    if (wn != t) {
                        WaitNode wnn;
                        if ((wnn = wn.next) == wn || pred.CasNext(wn, wnn)) {
                            return;
                        }
                    }

                    //
                    // The wait node is at the tail of the queue,take into
                    // account the *toUnlink* wait node.
                    //

                    WaitNode dp;
                    if ((dp = toUnlink) != null) {

                        //
                        // Try unlinking previous cancelled wait block.
                        //

                        WaitNode d, dn;
                        if ((d = dp.next) == dp ||
                            ((dn = d.next) != null && dp.CasNext(d, dn))) {
                            CasToUnlink(dp, null);
                        }
                        if (dp == pred) {
                            return;             // wn is an already saved node
                        }
                    } else if (CasToUnlink(null, pred)) {
                        return;
                    }
                }
            }
        }

        //
        // The non-blocking LIFO wait queue.
        //

        internal class NonBlockingLifoWaitQueue : NonBlockingWaitQueue {

            //
            // The top of the non-blocking stack that implements the queue.
            //

            private volatile WaitNode top;

            //
            // Enqueues the specified wait node.
            //

            internal override WaitNode Enqueue(WaitNode wn) {
                do {
                    WaitNode t;
                    wn.next = (t = top);
                    if (Interlocked.CompareExchange<WaitNode>(ref top, wn, t) == t) {
                        return null;
                    }
                } while (true);
            }

            //
            // Tries to dequeue a wait node and to lock the associated parker.
            //

            internal override WaitNode TryDequeueAndLock() {
                do {
                    WaitNode t;
                    if ((t = top) == null) {
                        return null;
                    }
                    if (Interlocked.CompareExchange<WaitNode>(ref top, t.next, t) == t &&
                        t.parker.TryLock()) {
                        return t;
                    }
                } while (true);
            }

            //
            // Returns true when the queue is empty.
            //

            internal override bool IsEmpty {
                get { return top == null; }
            }

            //
            // Unlinks the specified wait node from the stack.
            //

            internal override void Unlink(WaitNode wn, WaitNode ignored) {

                //
                // Absorb the cancelled wait nodes at the top of the stack.
                //

                WaitNode p;
                do {
                    if ((p = top) == null) {
                        return;
                    }
                    if (p.parker.IsLocked &&
                        Interlocked.CompareExchange<WaitNode>(ref top, p.next, p) == p) {
                        
                        //
                        // If the wait block was at the top of the stack, return.
                        //

                        if (p == wn) {
                            return;
                        }
                    } else {
                        break;
                    }
                } while (true);

                //
                // Compute a wait node that follows "wn" and try to unsplice
                // the wait node.
                //

                WaitNode past;
                if ((past = wn.next) != null && past.parker.IsLocked) {
                    past = past.next;
                }
                while (p != null && p != past) {
                    WaitNode n = p.next;
                    if (n != null && n.parker.IsLocked) {
                        p.CasNext(n, n.next);
                    } else {
                        p = n;
                    }
                }
            }
        }

        //
        // A lock protected queue of wait nodes.
        //

        internal struct WaitQueue {

            //
            // The first and last wait nodes.
            //

            internal WaitNode head;
            internal WaitNode tail;

            //
            // Enqueues a wait node at the tail of the queue.
            //

            internal void Enqueue(WaitNode wn) {
                if (head == null) {
                    head = wn;
                } else {
                    tail.next = wn;
                }
                tail = wn;
            }

            //
            // Enqueues a wait node at the front of the queue.
            //

            internal void EnqueueHead(WaitNode wn) {
                if ((wn.next = head) == null) {
                    tail = wn;
                }
                head = wn;
            }

            //
            // Dequeues a wait node from a non-empty queue.
            //

            internal WaitNode Dequeue() {
                WaitNode wn = head;
                if ((head = wn.next) == null) {
                    tail = null;
                }
                wn.next = wn;   // Mark the wait node as unlinked.
                return wn;
            }

            //
            // Returns true if the queue is empty.
            //

            internal bool IsEmpty { get { return head == null; } }

            //
            // Removes the specified wait node from the queue.
            //

            internal void Remove(WaitNode wn) {

                //
                // If the wait node was already removed, return.
                //

                if (wn.next == wn) {
                    return;
                }

                //
                // Locate the wait node and remove it.
                //

                WaitNode p = head;
                WaitNode pv = null;
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
        }

        /*++
         * 
         * Virtual ...
         * 
         --*/

        //
        // Tries to add a data item to the queue immediately.
        //

        public abstract bool TryAdd(T di);

        //
        // Executes the prologue of the TryAdd method.
        //

        internal abstract WaitNode TryAddPrologue(StParker pk, int key, T di, ref WaitNode hint);

        //
        // Cancels an add attempt.
        //

        internal virtual void CancelAddAttempt(WaitNode wn, WaitNode hint) { }

        //
        // Tries to take immediately a data item from the queue.
        //

        public abstract bool TryTake(out T di);

        //
        // Executes the prologue of the TryTake method.
        //

        internal abstract WaitNode TryTakePrologue(StParker pk, int key,
                                                   out T di, ref WaitNode hint);

        //
        // Executes the epilogue of the TryTake method.
        //

        internal virtual void TakeEpilogue() { }

        //
        // Cancels a take attempt.
        //

        internal abstract void CancelTakeAttempt(WaitNode wn, WaitNode hint);

        /*++
         * 
         * Generic API
         * 
         --*/

        //
		// Tries to add a data item to the queue, activating the specified
        // cancellers.
        //

		public bool TryAdd(T di, StCancelArgs cargs) {

            if (TryAdd(di)) {
                return true;
            }
            if (cargs.Timeout == 0) {
                return false;
            }
            WaitNode hint = null;
            WaitNode wn;
            StParker pk = new StParker();
            if ((wn = TryAddPrologue(pk, StParkStatus.Success, di, ref hint)) == null) {
                return true;
            }
            int ws = pk.Park(cargs);
            if (ws == StParkStatus.Success) {
                return true;
            }

            //
            // The add operation was cancelled; so, cancel the add attempt
            // and report the failure appropriately.
            //

            CancelAddAttempt(wn, hint);
            StCancelArgs.ThrowIfException(ws);
            return false;
        }

        //
        // Adds a data item to the queue unconditionally.
        //

        public virtual void Add(T di) {
            TryAdd(di, StCancelArgs.None);
        }

        //
        // Tries to add a data item to one of the specified subset of queues
        // (defined by offset and count), activating the specified cancellers.
        //

        public static int TryAddAny(StBlockingQueue<T>[] qs, int offset, int count,
                                    T di, StCancelArgs cargs) {
            int def = 0;
            int len = qs.Length;

            //
            // Try to add the data item immediately from one of the queues.
            //

            for (int j = offset, i = 0; i < count; i++) {
                StBlockingQueue<T> q = qs[j];
                if (q != null) {
                    if (q.TryAdd(di)) {
                        return (StParkStatus.Success + j);
                    }
                    def++;
                }
                if (++j >= len) {
                    j = 0;
                }
            }

            //
            // If the *qs* array contains only null references, throw the
            // ArgumentOuOfRangeException.
            //

            if (def == 0) {
                throw new ArgumentOutOfRangeException("qs: array contains only null references");
            }

            //
            // None of the specified queues allows an immediate add operation.
            // So, return failure if a null timeout was specified.
            //

            if (cargs.Timeout == 0) {
                return StParkStatus.Timeout;
            }

            //
            // Create a parker and execute the add-any prologue on the queues;
            // the loop is exited when we detect that the add operation was
            // accomplished.
            //

            StParker pk = new StParker();
            WaitNode[] wns = new WaitNode[len];
            WaitNode[] hints = new WaitNode[len];
            int lv = -1;
            for (int j = offset, i = 0; !pk.IsLocked && i < count; i++) {
                StBlockingQueue<T> q = qs[j];
                if (q != null) {
                    if ((wns[j] = q.TryAddPrologue(pk, (StParkStatus.Success + j), di,
                                                   ref hints[j])) == null) {
                        break;
                    }
                    lv = j;
                }
                if (++j >= len) {
                    j = 0;
                }
            }

            //
            // Park the current thread, activating the specified cancellers.
            //

            int ws = pk.Park(cargs);

            //
            // If the add operation succeed, compute the index of the
            // queue where we the data item was added.
            //

            int ti = -1;
            if (ws >= StParkStatus.Success) {
                ti = ws - StParkStatus.Success;
            }

            //
            // Cancel the add attempt on all queues where we inserted
            // wait blocks, except the one where we added the data item.
            //

            for (int j = offset, i = 0; i < count; i++) {
                WaitNode wn;
                if (j != ti && (wn = wns[j]) != null) {
                    qs[j].CancelAddAttempt(wn, hints[j]);
                }
                if (j == lv) {
                    break;
                }
                if (++j >= len) {
                    j = 0;
                }
            }

            //
            // Return success or failure appropriately.
            //

            if (ti != -1) {
                return ws;
            }
            StCancelArgs.ThrowIfException(ws);
            return StParkStatus.Timeout;
        }

        //
        // Adds a data item from the specified subset of queues, defined
        // by offset and count.
        //

        public static int AddAny(StBlockingQueue<T>[] qs, int offset, int count, T di) {
            return TryAddAny(qs, offset, count, di, StCancelArgs.None);
        }

        //
        // Adds a data item from one of the specified queues, activating
        // the specified cancellers.
        //

        public static int TryAddAny(StBlockingQueue<T>[] queues, T di, StCancelArgs cargs) {
            return TryAddAny(queues, 0, queues.Length, di, cargs);
        }

        //
        // Adds a data item from one of the specified queues.
        //

        public static int AddAny(StBlockingQueue<T>[] queues, out T di) {
            return TryTakeAny(queues, 0, queues.Length, out di, StCancelArgs.None);
        }

        //
        // Tries to take a data item from the queue, activating the
        // specified cancellers.
        //

        public bool TryTake(out T di, StCancelArgs cargs) {
            if (TryTake(out di)) {
                return true;
            }
            if (cargs.Timeout == 0) {
                return false;
            }
            StParker pk = new StParker();
            WaitNode wn, hint = null;
            if ((wn = TryTakePrologue(pk, StParkStatus.Success, out di, ref hint)) == null) {
                return true;
            }
            int ws = pk.Park(cargs);
            if (ws == StParkStatus.Success) {
                TakeEpilogue();
                di = wn.channel;
                return true;
            }

            //
            // The take was cancelled; so, cancel the take attempt and
            // report the failure appropriately.
            //
            
            CancelTakeAttempt(wn, hint);
            StCancelArgs.ThrowIfException(ws);
            return false;
        }

        //
        // Takes a data tem to the queue.
        //
        
        public T Take() {
            T di;
            TryTake(out di, StCancelArgs.None);
            return di;
        }

		//
		// Tries to take a data item from the specified subset of queues,
        // defined by offset and count, activating the specified cancellers.
		//

		public static int TryTakeAny(StBlockingQueue<T>[] qs, int offset, int count,
                                     out T di, StCancelArgs cargs) {
			int def = 0;
			int len = qs.Length;

			//
			// Try to take a data item immediately from one of the specified queues.
			//

			for (int j = offset, i = 0; i < count; i++) {
				StBlockingQueue<T> q = qs[j];
				if (q != null) {
                    if (q.TryTake(out di)) {
                        return (StParkStatus.Success + j);
                    }
					def++;
				}
                if (++j >= len) {
                    j = 0;
                }
			}

			//
			// If the *qs* array contains only null references, throw the
            // ArgumentException.
			//

            if (def == 0) {
                throw new ArgumentException("qs: array contains only null references");
            }

			//
			// None of the specified queues allows an immediate take operation.
            // So, return failure if a null timeout was specified.
            //

            di = default(T);
            if (cargs.Timeout == 0) {
                return StParkStatus.Timeout;
            }

            //
			// Create a parker and execute the take-any prologue on the
            // queues; the loop is exited when we detect that the take-any
            // operation was satisfied.
			//

			StParker pk = new StParker();
			WaitNode[] wns = new WaitNode[len];
            WaitNode[] hints  = new WaitNode[len];
            int lv = -1;
			for (int j = offset, i = 0; !pk.IsLocked && i < count; i++) {
				StBlockingQueue<T> q = qs[j];
				if (q != null) {
					if ((wns[j] = q.TryTakePrologue(pk, (StParkStatus.Success + j), out di,
                                                    ref hints[j])) == null) {
						break;
                    }
                    lv = j;
				}
                if (++j >= len) {
                    j = 0;
                }
			}

			//
			// Park the current thread, activating the specified cancellers.
			//

			int ws = pk.Park(cargs);

			//
			// If the take-any operation succeed, compute the index of the
            // queue where we the data item was taken, retrive the data item
            // and execute the take epilogue, if needed.
			//

            int ti = -1;
			if (ws >= StParkStatus.Success) {
                ti = ws - StParkStatus.Success;
                if (wns[ti] != null) {
                    di = wns[ti].channel;
                    qs[ti].TakeEpilogue();
                }
            }

			//
			// Cancel the take attempt on all queues where we inserted
            // wait blocks, except the one where we retrieved the data item.
			//

            for (int j = offset, i = 0; i < count; i++) {
                WaitNode wn;
                if (j != ti && (wn = wns[j]) != null) {
                    qs[j].CancelTakeAttempt(wn, hints[j]);
                }
                if (j == lv) {
                    break;
                }
                if (++j >= len) {
                    j = 0;
                }
            }

            //
            // Return success or failure appropriately.
            //

            if (ti != -1) {
                return ws;
            }
            StCancelArgs.ThrowIfException(ws);
            return StParkStatus.Timeout;
		}

        //
        // Takes a data item from the specified subset of queues, defined
        // by offset and count.
        //

		public static int TakeAny(StBlockingQueue<T>[] qs, int offset, int count, out T di) {
            return TryTakeAny(qs, offset, count, out di, StCancelArgs.None);
        }

		//
        // Tries to take a data item from one of the specified queues, 
        // activating the specified cancel sources.
		//

		public static int TryTakeAny(StBlockingQueue<T>[] queues, out T di,
                                     StCancelArgs cargs) {
			return TryTakeAny(queues, 0, queues.Length, out di, cargs);
		}

        //
        // Takes a data item from one of the specified queues.
        //

        public static int TakeAny(StBlockingQueue<T>[] queues, out T di) {
            return TryTakeAny(queues, 0, queues.Length, out di, StCancelArgs.None);
        }

        //
        // Registers a take with the blocking queue.
        //

        public StRegisteredTake<T> RegisterTake(StTakeCallback<T> callback, object cbState,
                                             int timeout, bool executeOnce) {
            return new StRegisteredTake<T>(this, callback, cbState, timeout, executeOnce);
        }
    }
}
