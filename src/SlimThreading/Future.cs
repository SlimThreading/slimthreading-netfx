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

    //
    // This class implements a future for the type T.
    //

    public sealed class StFuture<T> : StNotificationEventBase {
        
        //
        // The future state is non-zero if the futures value is available.
        //

        private volatile int wasSet;
        private T _value;

        public StFuture(int spinCount) : base(false, spinCount) { }

        public StFuture() : base(false, 0) { }

        //
        // Waits until the future's value is available, activating the
        // specified cancellers.
        //

        public bool Wait(out T result, StCancelArgs cargs) {
            if (waitEvent.Wait(cargs) == StParkStatus.Success) {
                result = _value;
                return true;
            }

            result = default(T);
            return false;
        }

        //
        // Get and set the future value.
        //

        public T Value {
            get {
                if (waitEvent.IsSet) {
                    return _value;
                }
                waitEvent.Wait(StCancelArgs.None);
                return _value;
            }

            set {
                if (wasSet == 0 && Interlocked.Exchange(ref wasSet, 1) == 0) {
                    _value = value;
                    waitEvent.Set();
                    return;
                }

                throw new InvalidOperationException("The future value is alredy set");
            }
        }

        //
        // Returns the expection that must be thrown when the signal
        // operation fails.
        //

        internal override Exception _SignalException {
            get { return new InvalidOperationException("Futures can't be signalled"); }
        }
    }
}
