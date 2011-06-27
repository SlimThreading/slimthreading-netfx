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

using System.Threading;

#pragma warning disable 0420

namespace SlimThreading {

    //
    // This class implements a synchronizer that is used as base class
    // by the fair lock and the synchronization event.
    //

    public class Mutant : StWaitable {
        private const int ACQUIRE = 1;
        private const int LOCKED_ACQUIRE = WaitBlock.LOCKED_REQUEST | ACQUIRE;

        //
        // The boolean state of the mutant (signalled/non-signalled) is stored
        // in the *head.next* field, as follows:
        // - head.next == SET: the mutant is signalled and its queue is empty;
        // - head.next == null: the mutant is non-signalled and its queue is empty;
        // - others: the mutant isn't signalled and its queue is non-empty.
        //

        private static readonly WaitBlock SET = WaitBlock.SENTINEL;

        private volatile WaitBlock head;
        private volatile WaitBlock tail;

        //
        // The predecessor of a wait block that must be unlinked
        // when the right conditions are met.
        //

        private volatile WaitBlock toUnlink;

        //
        // The number of spin cycles executed by the first waiter
        // thread before it blocks.
        //

        private readonly int spinCount;

        internal Mutant(bool initialState, int sc) {
            head = tail = new WaitBlock();
            if (initialState) {
                head.next = SET;
            }
            spinCount = Platform.IsMultiProcessor ? sc : 0;
        }

        internal override bool _AllowsAcquire {
            get { return head.next == SET; }
        }

        internal override bool _TryAcquire() {
            return head.next == SET &&
                   Interlocked.CompareExchange(ref head.next, null, SET) == SET;
        }

        internal override void _UndoAcquire() {
            Release();
        }

        //
        // Releases the mutant and returns its previous state (true
        // if SET or false otherwise).
        //

        protected bool Release() {
            do {
                WaitBlock h, hn;
                if ((hn = (h = head).next) == SET) {
                    return true;
                } 

                //
                // If the mutant's queue is empty, try to set the *head.next*
                // field to SET.
                //

                if (hn == null) {
                    if (Interlocked.CompareExchange(ref head.next, SET, null) == null) {
                        return false;
                    }
                    continue;
                }

                if (TryAdvanceHead(h, hn)) {
                    StParker pk;
                    if ((pk = hn.parker).TryLock() || hn.request == LOCKED_ACQUIRE) {
                        pk.Unpark(hn.waitKey);

                        //
                        // If this is a wait-any wait block, we are done;
                        // otherwise, keep trying to release other waiters.
                        //

                        if (hn.waitType == WaitType.WaitAny) {
                            return false;
                        }
                    }
                }
            } while (true);
        }

        //
        // Enqueues the specified wait block in the wait queue as a locked 
        // acquire request. This is used to implement wait morphing. When 
        // the method is called the mutant is non-signalled.
        //

        internal void EnqueueLockedWaiter(WaitBlock wb) {
            wb.request = LOCKED_ACQUIRE;
            wb.next = null;
            WaitBlock pred;
            EnqueueWaiter(wb, out pred);
        }

        internal override bool _Release() {
            if (head.next != SET) {
                Release();
            }
            return true;
        }

        internal override WaitBlock _WaitAnyPrologue(StParker pk, int key,
                                                     ref WaitBlock hint, ref int sc) {
            WaitBlock wb = null;
            do {
                if (_TryAcquire()) {
                    return null;
                }
                
                if (wb == null) {
                    wb = new WaitBlock(pk, WaitType.WaitAny, ACQUIRE, key);
                }

                WaitBlock pred;
                if (EnqueueWaiter(wb, out pred)) {
                    sc = ((hint = pred) == head) ? spinCount : 0;
                    return wb;
                }
            } while (true);
        }

        internal override WaitBlock _WaitAllPrologue(StParker pk, ref WaitBlock hint,
                                                     ref int sc) {
            WaitBlock wb = null;
            if (_AllowsAcquire) {
                return null;
            }
                
            if (wb == null) {
                wb = new WaitBlock(pk, WaitType.WaitAll, ACQUIRE, StParkStatus.StateChange);
            }

            WaitBlock pred;
            if (EnqueueWaiter(wb, out pred)) {
                sc = ((hint = pred) == head) ? spinCount : 0;
                return wb;
            }

            return null;
        }

        internal override void _CancelAcquire(WaitBlock wb, WaitBlock hint) {
            while (hint.next == wb) {

                //
                // Remove the cancelled wait blocks that are at the front
                // of the queue.
                //

                WaitBlock h, hn;
                if ((hn = (h = head).next) != null && hn != SET &&
                    hn.parker.IsLocked && hn.request != LOCKED_ACQUIRE) {
                    TryAdvanceHead(h, hn);
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
                    if ((wbn = wb.next) == wb || hint.CasNext(wb, wbn)) {
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
                    if (dp == hint) {
                        return;             // *wb* is an already the saved node.
                    }
                } else if (CasToUnlink(null, hint)) {
                    return;
                }
            }
        }

        private bool EnqueueWaiter(WaitBlock wb, out WaitBlock pred) {
            do {
                WaitBlock t, tn;
                if ((tn = (t = tail).next) == SET) {
                    pred = null;
                    return false;
                }

                if (tn != null) {
                    AdvanceTail(t, tn);
                    continue;
                }

                if (Interlocked.CompareExchange(ref t.next, wb, null) == null) {
                    AdvanceTail(t, wb);
                    pred = t;
                    return true;
                }
            } while (true);
        }

        private void AdvanceTail(WaitBlock t, WaitBlock nt) {
            if (t == tail) {
                Interlocked.CompareExchange(ref tail, nt, t);
            }
        }

        private bool TryAdvanceHead(WaitBlock h, WaitBlock nh) {
            if (h == head && Interlocked.CompareExchange(ref head, nh, h) == h) {
                h.next = h; // Mark the old head as unlinked.
                return true;
            }
            return false;
        }

        private bool CasToUnlink(WaitBlock tu, WaitBlock ntu) {
            return (toUnlink == tu &&
                    Interlocked.CompareExchange(ref toUnlink, ntu, tu) == tu);
        }
    }
}
