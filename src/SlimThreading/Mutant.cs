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
using System.Threading;

#pragma warning disable 0420

namespace SlimThreading {

    //
    // This class implements a synchronizer that is used as base class
    // with the fair lock and the synchronization event.
    //

    public class Mutant : StWaitable {

        //
        // Types of acquire requests.
        //

        private const int ACQUIRE = 1;
        private const int LOCKED_ACQUIRE = (WaitBlock.LOCKED_REQUEST | ACQUIRE);

        //
        // The boolean state of the mutant (signalled/non-signalled) is stored
        // in the *head.next* field, as follows:
        // - head.next == SET: the mutant is signalled and its queue is empty;
        // - head.next == null: the mutant is non-signalled and its queue is empty;
        // - others: the mutant isn't signalled and its queue is non-empty.
        //

        internal static WaitBlock SET = new WaitBlock();

        //
        // The wait queue is a non-blocking queue.
        //

        internal volatile WaitBlock head;
        private volatile WaitBlock tail;

        //
        // The predecessor of a wait block that must be unlinked
        // when the right conditions are met.
        //

        private volatile WaitBlock toUnlink;

        //
        // The number of spin cycles executed by the first waiter
        // thread before it blocks on the park spot.
        //

        private readonly int spinCount;

        //
        // Constructor.
        //

        internal Mutant(bool initialState, int sc) {
            head = tail = new WaitBlock();
            if (initialState) {
                head.next = SET;
            }
            spinCount = Platform.IsMultiProcessor ? sc : 0;
        }

        //
        // Advances the queue's head.
        //

        private bool AdvanceHead(WaitBlock h, WaitBlock nh) {
            if (h == head && Interlocked.CompareExchange<WaitBlock>(ref head, nh, h) == h) {
                h.next = h;     // Mark the old head as unlinked.
                return true;
            }
            return false;
        }

        //
        // Advances the queue tail.
        //

        private bool AdvanceTail(WaitBlock t, WaitBlock nt) {
            return (t == tail && Interlocked.CompareExchange<WaitBlock>(ref tail, nt, t) == t);
        }

        //
        // CASes on the *toUnlink* field.
        //

        private bool CasToUnlink(WaitBlock tu, WaitBlock ntu) {
            return (toUnlink == tu &&
                    Interlocked.CompareExchange<WaitBlock>(ref toUnlink, ntu, tu) == tu);
        }

        //
        // Returns true if the mutant allows an immediate acquire.
        //

        internal override bool _AllowsAcquire {
            get { return (head.next == SET); }
        }

        //
        // Tries to acquire the mutant immediately.
        //

        internal override bool _TryAcquire() {
            return (head.next == SET &&
                    Interlocked.CompareExchange<WaitBlock>(ref head.next, null, SET) == SET);
        }

        //
        // Tries to acquire a busy mutant, activating the specified cancellers.
        //

        internal bool SlowTryAcquire(StCancelArgs cargs) {
            WaitBlock wb = null, pred;
            do {

                //
                // If the mutant is set, try to reset it; if succeed,
                // return success.
                //

                if (head.next == SET) {
                    if (Interlocked.CompareExchange<WaitBlock>(ref head.next, null, SET) == SET) {
                        return true;
                    }
                    continue;
                }

                //
                // The mutant is reset; so, create a wait block to insert
                // in the wait queue.
                //

                if (wb == null) {
                    wb = new WaitBlock(WaitType.WaitAny, ACQUIRE);
                }

                //
                // Do the necessary consistency checks before try to insert
                // the wait block in the mutant's queue; if the queue is in a
                // quiescent state, try to perform the insertion.
                //

                WaitBlock t, tn;
                if ((tn = (t = tail).next) == SET) {
                    continue;
                }
                if (tn != null) {
                    AdvanceTail(t, tn);
                    continue;
                }
                if (Interlocked.CompareExchange<WaitBlock>(ref t.next, wb, null) == null) {
                    AdvanceTail(t, wb);

                    //
                    // Save the predecessor of the wait block and exit the loop.
                    //

                    pred = t;
                    break;
                }
            } while (true);

            //
            // Park the current thread, activating the specified cancellers,
            // and spinning, if appropriate.
            //

            int ws = wb.parker.Park((head == pred) ? spinCount : 0, cargs);

            //
            // If we acquired the mutant, return success. Otherwise, unlink the
            // wait block from the wait queue and report the failure appropriately
            //

            if (ws == StParkStatus.Success) {
                return true;
            }
            Unlink(wb, pred);
            StCancelArgs.ThrowIfException(ws);
            return false;
        }

        //
        // Releases the mutant.
        //

        internal bool SlowRelease() {

            //
            // If there are waiter threads on the mutant's queue, release
            // the appropriate waiters.
            //

            do {

                //
                // If the mutant is already signalled, return true.
                //

                WaitBlock h, hn;
                if ((hn = (h = head).next) == SET) {
                    return true;
                }

                //
                // If the mutant's queue is empty, try to set the *head.next*
                // field to SET and, if succeed, return.
                //

                if (hn == null) {
                    if (Interlocked.CompareExchange<WaitBlock>(ref head.next, SET, null) == null) {
                        return false;
                    }
                    continue;
                }

                //
                // The wait queue seems non-empty; so, try to remove the wait
                // block that is at the front of the queue.
                //

                if (AdvanceHead(h, hn)) {

                    //
                    // Try to lock the associated parker and, if succeed, unpark
                    // its owner thread.
                    //

                    StParker pk;
                    if ((pk = hn.parker).TryLock() || hn.request < 0) {
                        pk.Unpark(hn.waitKey);

                        //
                        // If this is a wait-any wait block, we are done;
                        // otherwise, keep trying to release another waiters.
                        //

                        if (hn.waitType == WaitType.WaitAny) {
                            return false;
                        }
                    }
                }
            } while (true);
        }

        //
        // Unlinks the specified wait block from the wait queue.
        //

        private void Unlink(WaitBlock wb, WaitBlock pred) {
            while (pred.next == wb) {

                //
                // Remove the cancelled wait blocks that are at the front
                // of the queue.
                //

                WaitBlock h, hn;
                if (((hn = (h = head).next) != null && hn != SET) &&
                    (hn.parker.IsLocked && hn.request > 0)) {
                    AdvanceHead(h, hn);
                    continue;
                }

                //
                // If the queue is empty, return.
                //

                WaitBlock t, tn;
                if ((t = tail) == h) {
                    return;
                }

                //
                // Do the necessary consistency checks before trying to
                // unlink the wait block.
                //

                if (t != tail) {
                    continue;
                }
                if ((tn = t.next) != null) {
                    AdvanceTail(t, tn);
                    continue;
                }

                //
                // If the wait block is not at the tail of the queue, try
                // to unlink it.
                //

                if (wb != t) {
                    WaitBlock wbn;
                    if ((wbn = wb.next) == wb || pred.CasNext(wb, wbn)) {
                        return;
                    }
                }

                //
                // The wait block is at the tail of the queue; so, take
                // into account the *toUnlink* wait block.
                //

                WaitBlock dp;
                if ((dp = toUnlink) != null) {
                    WaitBlock d, dn;
                    if ((d = dp.next) == dp ||
                        ((dn = d.next) != null && dp.CasNext(d, dn))) {
                        CasToUnlink(dp, null);
                    }
                    if (dp == pred) {
                        return;             // *wb* is an already the saved node.
                    }
                } else if (CasToUnlink(null, pred)) {
                    return;
                }
            }
        }

        //
        // Enqueues the specified wait block in the wait queue
        // as a locked acquire request.
        //
        // NOTE: When this method is called the mutant is non-signalled.
        //

        internal void EnqueueLockedWaiter(WaitBlock wb) {
            wb.request = LOCKED_ACQUIRE;
            wb.next = null;
            do {

                //
                // Do the necessary consistency checks in order to add the
                // wait block to the lock's queue.
                //

                WaitBlock t, tn;
                if ((tn = (t = tail).next) != null) {
                    AdvanceTail(t, tn);
                    continue;
                }

                //
                // The queue is in a consistent state, try to perform
                // the insert.
                //

                if (Interlocked.CompareExchange<WaitBlock>(ref t.next, wb, null) == null) {
                    AdvanceTail(t, wb);
                    return;
                }
            } while (true);
        }

        //
        // Releases the mutant if it is non-signalled.
        //

        internal override bool _Release() {
            if (head.next != SET) {
                SlowRelease();
                return true;
            }
            return false;
        }

        //
        // Executes the prologue of the Waitable.WaitAny method.
        //

        internal override WaitBlock _WaitAnyPrologue(StParker pk, int key,
                                                     ref WaitBlock hint, ref int sc) {
            WaitBlock wb = null;
            do {

                //
                // If the mutant seems set, try to reset it. If we reset
                // the mutant, try to lock the parker and, if succeed, self
                // unpark the current thread.
                //

                if (head.next == SET) {
                    if (Interlocked.CompareExchange<WaitBlock>(ref head.next, null, SET) == SET) {
                        if (pk.TryLock()) {
                            pk.UnparkSelf(key);
                        } else {

                            //
                            // The parker is already lock, which means that the
                            // wait-any operation was already accomplished. So,
                            // release the mutant, unding the previous acquire.
                            //

                            SlowRelease();
                        }
                        return null;
                    }
                    continue;
                }

                //
                // The mutant is reset. If this is the first loop iteration,
                // create a wait block to insert in the wait queue.
                //

                if (wb == null) {
                    wb = new WaitBlock(pk, WaitType.WaitAny, ACQUIRE, key);
                }

                //
                // Do the necessary consistency checks before try to
                // insert a wait block in the event's queue.
                //

                WaitBlock t, tn;
                if ((tn = (t = tail).next) == SET) {
                    continue;
                }
                if (tn != null) {
                    AdvanceTail(t, tn);
                    continue;
                }

                //
                // The queue is in a consistent state, try to perform
                // the insertion.
                //

                if (Interlocked.CompareExchange<WaitBlock>(ref t.next, wb, null) == null) {
                    AdvanceTail(t, wb);

                    //
                    // Return the inserted wait block, its predecessor and
                    // the sugested spin count.
                    //

                    sc = ((hint = t) == head) ? spinCount : 0;
                    return wb;
                }
            } while (true);
        }

        //
        // Executes the prologue of the Waitable.WaitAll method.
        //

        internal override WaitBlock _WaitAllPrologue(StParker pk, ref WaitBlock hint,
                                                     ref int sc) {
            WaitBlock wb = null;
            do {

                //
                // If the mutant can be immediately acquired, lock the our parker
                // and, if this is the last cooperative release, self unpark the
                // current thread.
                //

                if (_AllowsAcquire) {
                    if (pk.TryLock()) {
                        pk.UnparkSelf(StParkStatus.StateChange);
                    }
                    return null;
                }

                //
                // The mutant seems non-signalled. If this is the first loop
                // iteration, create a wait block to insert in the wait queue.
                //

                if (wb == null) {
                    wb = new WaitBlock(pk, WaitType.WaitAll, ACQUIRE, StParkStatus.StateChange);
                }

                //
                // Do the necessary consistency checks in order to insert the
                // wait block in the queue; if the queue is already in a consistent
                // state, try to perform the insertion.
                //

                WaitBlock t, tn;
                if ((tn = (t = tail).next) == SET) {
                    continue;
                }
                if (tn != null) {
                    AdvanceTail(t, tn);
                    continue;
                }
                if (Interlocked.CompareExchange<WaitBlock>(ref t.next, wb, null) == null) {
                    AdvanceTail(t, wb);

                    //
                    // Return the inserted wait block, its predecessor and
                    // the spin count for this wait block.
                    //

                    sc = ((hint = t) == head) ? spinCount : 0;
                    return wb;
                }
            } while (true);
        }

        //
        // Undoes a previous acquire.
        //

        internal override void _UndoAcquire() {
            SlowRelease();
        }

        //
        // Cancels the specified acquire attempt.
        //

        internal override void _CancelAcquire(WaitBlock wb, WaitBlock hint) {
            Unlink(wb, hint);
        }
    }
}
