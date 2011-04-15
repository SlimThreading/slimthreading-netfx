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
    // This value type implements a lock designed to synchronize
    // once initialization.
    //

    public struct StInitOnceLock {

        //
        // Distinguished values used for the lock state.
        //

        private const StParker FREE = null;
        private static readonly StParker BUSY = new StParker();
        private static readonly StParker AVAILABLE = new StParker();

        //
        // Status values used when the waiter threads are released.
        //

        private const int STATUS_AVAILABLE = StParkStatus.Success;
        private const int STATUS_INIT = StParkStatus.Success + 1;

        //
        // The lock state - starts null which means FREE.
        //

        private volatile StParker state;

        //
        // Acquires the init once lock and returns true to signal that
        // the current thread must perform the initialization;
        // otherwise, return false which means that the target is
        // already available.
        //

        public bool TryInit(int spinCount) {
            if (state == AVAILABLE) {
                return false;
            }
            return SlowTryInit(spinCount);
        }

        public bool TryInit() {
            if (state == AVAILABLE) {
                return false;
            }
            return SlowTryInit(0);
        }

        //
        // Slow pass of the TryInitEx method.
        //

        private bool SlowTryInit(int spinCount) {
            StParker s;
            do {
                if ((s = state) == FREE &&
                    Interlocked.CompareExchange<StParker>(ref state, BUSY, FREE) == FREE) {
                    return true;
                }
                if (s == AVAILABLE) {
                    return false;
                }
                if (spinCount-- <= 0) {
                    break;
                }
                Platform.SpinWait(1);
            } while (true);

            //
            // The initialization is taking place. So, create a locked parker
            // and insert it in the wait queue, if the lock remains busy.
            //

            StParker pk = new StParker(0);
            do {
                if ((s = state) == AVAILABLE) {
                    return false;
                }
                if (s == FREE &&
                    Interlocked.CompareExchange<StParker>(ref state, BUSY, FREE) == FREE) {
                    return true;
                }
                pk.pnext = s;
                if (Interlocked.CompareExchange<StParker>(ref state, pk, s) == s) {
                    break;
                }
            } while (true);

            //
            // Park the current thread and, after release, return the
            // appropriate value.
            //

            return (pk.Park() == STATUS_INIT);
        }

        //
        // Signals that the initialization is completed.
        //

        public void InitCompleted() {
            StParker p = Interlocked.Exchange<StParker>(ref state, AVAILABLE);
            while (p != BUSY) {
                p.Unpark(STATUS_AVAILABLE);
                p = p.pnext;
            }
        }

        //
        // Signals that initialization failed.
        //

        public void InitFailed() {
            do {
                StParker p;

                //
                // if the wait list is empty, free the lock.
                //

                if ((p = state) == BUSY &&
                    Interlocked.CompareExchange<StParker>(ref state, FREE, BUSY) == BUSY) {
                    return;
                }

                //
                // It seems that there are waiter threads; so, try to
                // release one of them to retry the initialization of
                // the target.
                //

                if (Interlocked.CompareExchange<StParker>(ref state, p.pnext, p) == p) {
                    p.Unpark(STATUS_INIT);
                    return;
                }
            } while (true);
        }
    }
}
