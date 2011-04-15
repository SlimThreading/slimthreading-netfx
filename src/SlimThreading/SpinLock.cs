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
    // This value type implements a user-mode spinlock.
    //

    public struct SpinLock {

        //
        // Constants used with lock state.
        //

        private const int FREE = 0;
        private const int BUSY = 1;

        //
        // The spin lock state and wait queue.
        //
        // NOTE: The wait queue is a non-blocking stack of parkers.
        //

        private volatile int state;
        private volatile StParker top;

        //
        // The number of spin cycles executed before each thread inserts
        // a wait node in the wait queue.
        //

        private int spinCount;

        //
        // Constructors.
        //

        public SpinLock(int sc) {
            state = FREE;
            top = null;
            spinCount = Platform.IsMultiProcessor ? sc : 0;
        }

        //
        // Tries to acquire the spin lock if it is free.
        //

        public bool TryEnter() {
            return (state == FREE && Interlocked.CompareExchange(ref state, BUSY, FREE) == FREE);
        }

        //
        // Acquires the spin lock unconditionally.
        //

        public void Enter() {
            if (state == FREE && Interlocked.CompareExchange(ref state, BUSY, FREE) == FREE) {
                return;
            }
            SlowEnter();
        }

        //
        // Slow path to acquire the spin lock.
        //

        private void SlowEnter() {
            StParker pk = null;
            do {

                //
                // First, try to acquire the spin lock, spinning for the
                // specified number of cycles, if the wait queue is empty.
                //

                int sc = spinCount;
                do {
                    if (state == FREE &&
                        Interlocked.CompareExchange(ref state, BUSY, FREE) == FREE) {
                        return;
                    }
                    if (top != null || sc-- <= 0) {
                        break;
                    }
                    Platform.SpinWait(1);
                } while (true);

                //
                // The spin lock is busy. So, create, or reset, a parker and
                // insert it in the wait queue.
                //

                if (pk == null) {
                    pk = new StParker(0);
                } else {
                    pk.Reset(0);
                }
                do {
                    StParker t;
                    pk.pnext = (t = top);
                    if (Interlocked.CompareExchange<StParker>(ref top, pk, t) == t) {
                        break;
                    }
                } while (true);

                //
                // Since that the lock can become free after the parker is
                // inserted in the wait queue, we must retry to acquire the spinlock
                // if it seems free.
                //
                // NOTE: We don't remove the parker from the wait queue, because
                //       it will be surely removed next time the lock is release.
                //

                if (state == FREE &&
                    Interlocked.CompareExchange(ref state, BUSY, FREE) == FREE) {
                    return;
                }

                //
                // Park the current thread and, after release, retry the
                // spin lock acquire.
                //

                pk.Park();
            } while (true);
        }

        //
        // Exits the lock.
        //

        public void Exit() {

            //
            // Since that atomic operations on references are more
            // expensive than on integers, we optimize the release when
            // the spin lock's wait queue is empty. However, when the queue
            // seems empty before the lock is released, but it's seen
            // non-empty after the lock is released, our algorithm resorts
            // to two atomic instructions.
            //

            if (top == null) {
                Interlocked.Exchange(ref state, FREE);
                if (top == null) {
                    return;
                }
            } else {
                state = FREE;
            }

            //
            // Unpark all waiting threads.
            //
            // NOTE: Since that the spin lock's queue is implemented with
            //       a stack, we build another stack in order to unpark the
            //       waiting thread according to its arrival order.
            //

            StParker p = Interlocked.Exchange<StParker>(ref top, null);
            StParker ws = null, n;
            while (p != null) {
                n = p.pnext;
                p.pnext = ws;
                ws = p;
                p = n;
            }
            while (ws != null) {
                n = ws.pnext;
                ws.Unpark(StParkStatus.Success);
                ws = n;
            }
        }
    }
}
