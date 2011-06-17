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

namespace SlimThreading {

    //
    // This structure is used to specify the cancellers for
    // blocking acquire operations.
    //

    public struct StCancelArgs {

        //
        // The specified timeout, disabled if set to -1.
        //

        public int Timeout { get; private set; }

        //
        // The alerter. Can be null.
        //

        public StAlerter Alerter { get; private set; }

        //
        // Indicates if the wait can be cancelled by thread interruption.
        //

        public bool Interruptible { get; private set; }

        //
        // CancelArgs used to specify no cancellers.
        //

        public static readonly StCancelArgs None = new StCancelArgs(-1, null, false);

        public StCancelArgs(int timeout, StAlerter alerter, bool interruptible) {
            if (timeout < -1) {
                throw new ArgumentOutOfRangeException("timeout", timeout, "Wrong timeout value");
            }

            this = new StCancelArgs();
            Timeout = timeout;
            Alerter = alerter;
            Interruptible = interruptible;
        }

        public StCancelArgs(int timeout) : this(timeout, null, false) { }

        public StCancelArgs(TimeSpan timeout) : this(timeout.Milliseconds, null, false) { }

        public StCancelArgs(StAlerter alerter) : this(-1, alerter, false) { }

        public StCancelArgs(bool interruptible) : this(-1, null, interruptible) { }
        
        public StCancelArgs(int timeout, bool interruptible) : this(timeout, null, interruptible) { }

        public StCancelArgs(TimeSpan timeout, bool interruptible) : this(timeout.Milliseconds, null, interruptible) { }
 
        public StCancelArgs(int timeout, StAlerter alerter) : this(timeout, alerter, false) { }

        public StCancelArgs(TimeSpan timeout, StAlerter alerter) : this(timeout.Milliseconds, alerter, false) { }

        public StCancelArgs(StAlerter alerter, bool interruptible) : this(-1, alerter, interruptible) { }

        //
        // Adjusts the timeout value, returning false if the timeout 
        // has expired.
        //

        public bool AdjustTimeout(ref int lastTime) {
            if (Timeout == System.Threading.Timeout.Infinite) {
                return true;
            }
            
            int now = Environment.TickCount;
            int e = (now == lastTime) ? 1 : (now - lastTime);
            if (Timeout <= e) {
                return false;
            }

            Timeout -= e;
            lastTime = now;
            return true;
        }

        //
        // Thows the cancellation exception, if appropriate;
        // otherwise, does noting.
        //

        public static void ThrowIfException(int ws) {
            switch (ws) {
                case StParkStatus.Alerted: throw new StThreadAlertedException();
                case StParkStatus.Interrupted: throw new ThreadInterruptedException();
                default: return;
            }
        }

        //
        // Postpones the cancellation due to the specifed wait status.
        //

        internal static void PostponeCancellation(int ws) {
            if (ws == StParkStatus.Interrupted) {
                Thread.CurrentThread.Interrupt();
            }
        }
    }
}