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
    // This value type implements a lock designed to synchronize
    // one-time-only initialization.
    //

    public struct StInitOnceLock {

        //
        // Distinct values used for the lock state. The lock starts in the FREE
        // state. When a thread first calls TryInit the state advances to BUSY.
        // All subsequent calls to TryInit will block. The thread must complete
        // the initialization and call InitCompleted, advancing the state to 
        // AVAILABLE and unparking all waiting threads. If the initialization 
        // fails then the thread must call InitFailed to unpark a waiter thread 
        // that will retry the initialization, becoming responsible for advancing
        // the lock's state. If there are no waiters the state reverts back to FREE.
        //

        private const StParker FREE = null;
        private static readonly SentinelParker BUSY = new SentinelParker();
        private static readonly SentinelParker AVAILABLE = new SentinelParker();

        //
        // Status values used when the waiter threads are released.
        //

        private const int STATUS_AVAILABLE = StParkStatus.Success;
        private const int STATUS_INIT = StParkStatus.Success + 1;

        //
        // The lock state - starts null which means FREE.
        //

        private volatile StParker state;

        public bool IsInitializationPerformed {
            get { return state == AVAILABLE; }
        }

        //
        // Acquires the init once lock. Returns true to signal that the current
        // thread must perform the initialization or false which means that the 
        // target is already available.
        //

        public bool TryInit(int spinCount) {
            return state != AVAILABLE && SlowTryInit(spinCount);
        }

        public bool TryInit() {
            return state != AVAILABLE && SlowTryInit(0);
        }

        //
        // Signals that the initialization is completed.
        //

        public void InitCompleted() {
            var p = Interlocked.Exchange(ref state, AVAILABLE);
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

                if ((p = state) == BUSY &&
                    Interlocked.CompareExchange(ref state, FREE, BUSY) == BUSY) {
                    return;
                }

                if (Interlocked.CompareExchange(ref state, p.pnext, p) == p) {
                    p.Unpark(STATUS_INIT);
                    return;
                }
            } while (true);
        }

        private bool SlowTryInit(int spinCount) {
            StParker s;
            do {
                if ((s = state) == FREE &&
                    Interlocked.CompareExchange(ref state, BUSY, FREE) == FREE) {
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

            var pk = new StParker(0);
            do {
                if ((s = state) == FREE &&
                    Interlocked.CompareExchange(ref state, BUSY, FREE) == FREE) {
                    return true;
                }
                if (s == AVAILABLE) {
                    return false;
                }

                pk.pnext = s;
                if (Interlocked.CompareExchange(ref state, pk, s) == s) {
                    break;
                }
            } while (true);

            return pk.Park() == STATUS_INIT;
        }
    }
}
