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
using System.Threading;

#pragma warning disable 0420

namespace SlimThreading {
    public class StAlmostFairLock {
        private const int ACQUIRE = 1;
        private const int CANDIDATE = 2;
      
        private const int FREE = 0;
        private const int BUSY = 1;
        private volatile int state = FREE;

        private volatile WaitBlock head;
        private volatile WaitBlock tail;

        private readonly int spinCount;

        public StAlmostFairLock(int sc) : this() {
            spinCount = Platform.IsMultiProcessor ? sc : 0;
        }

        public StAlmostFairLock() {
            head = tail = new WaitBlock();
        }

        //
        // Tries to acquire the lock immediately.
        //

        public bool TryEnter() {
            return state == FREE &&
                   Interlocked.CompareExchange(ref state, BUSY, FREE) == FREE;
        }

        //
        // Acquires the lock unconditionally.
        //

        public void Enter() {
            Enter(StCancelArgs.None);
        }

        //
        // Tries to acquire the lock, activating the specified
        // cancellers.
        //

        public bool Enter(StCancelArgs cargs) {
            return TryEnter() || (cargs.Timeout != 0 ? SlowEnter(cargs) : false);
        }

        //
        // Frees the lock and selects a candidate owner from the queue
        // of waiting threads.
        //

        public void Exit() {
            WaitBlock wb = head;
            bool unpark = false;
            StParker pk = null;

            while ((wb = wb.next) != null && wb.request != CANDIDATE) {
                pk = wb.parker;
                if (pk.TryLock()) {
                    wb.request = CANDIDATE;
                    unpark = true;
                    break;
                }
            }

            state = FREE;

            if (unpark) {
                pk.Unpark(StParkStatus.Success);
            }
        }

        private bool SlowEnter(StCancelArgs cargs) {
            int lastTime = cargs.Timeout != Timeout.Infinite ? Environment.TickCount : 0;
            bool timeRemaining = true;
            WaitBlock wb = EnqueueWaiter();

            do {
                if (TryEnter()) {
                    Cleanup(wb);
                    return true;
                }

                if (!timeRemaining) {
                    return false;
                }

                int ws = wb.parker.Park(head.next == wb ? spinCount : 0, cargs); 

                if (ws != StParkStatus.Success) {
                    StCancelArgs.ThrowIfException(ws);
                    return false;
                }

                if (TryEnter()) {
                    Cleanup(wb);
                    return true;
                }
                
                //
                // We failed to acquire the lock so we must clear the current 
                // thread as the candidate owner. After doing so we must recheck 
                // the state of lock. If the wait timed out, we exit the loop
                // before parking again.
                //

                if ((timeRemaining = cargs.AdjustTimeout(ref lastTime))) {
                    wb.parker.Reset();
                }

                //
                // Avoid a release-followed-by-acquire hazard.
                // 

                Interlocked.Exchange(ref wb.request, ACQUIRE);
            } while (true);
        }

        private WaitBlock EnqueueWaiter() {
            var wb = new WaitBlock(ACQUIRE);
            do {
                WaitBlock t, tn;
                if ((tn = (t = tail).next) != null) {
                    AdvanceTail(t, tn);
                    continue;
                }

                if (Interlocked.CompareExchange(ref t.next, wb, null) == null) {
                    AdvanceTail(t, wb);
                    return wb;
                }
            } while (true);
        }

        private void AdvanceTail(WaitBlock t, WaitBlock nt) {
            if (t == tail) {
                Interlocked.CompareExchange(ref tail, nt, t);
            }
        }

        private void Cleanup(WaitBlock toRemove) {
            WaitBlock prev;
            WaitBlock wb = (prev = head).next;
            WaitBlock t = tail;

            while (wb != null && wb != t) {
                if (wb == toRemove || wb.parker.IsLocked) {
                    if (prev == head) {
                        head = prev = wb;
                    } else {
                        prev.next = wb.next;
                    }
                } else {
                    prev = wb;
                }

                wb = wb.next;
            }
        }
    }
}
