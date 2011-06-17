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
using System.Collections.Generic;
using System.Threading;

namespace SlimThreading {
    public static class WaitHandleExtensions {

        private static readonly Dictionary<Type, Action<WaitHandle>> undoAcquire;

        static WaitHandleExtensions() {
            undoAcquire = new Dictionary<Type, Action<WaitHandle>> {
                { typeof(Mutex), wh => ((Mutex)wh).ReleaseMutex() },
                { typeof(Semaphore), wh => ((Semaphore)wh).Release() },
                { typeof(AutoResetEvent), wh => ((AutoResetEvent)wh).Set() },
                { typeof(ManualResetEvent), wh => { } } 
            };
        }

        public static void UndoAcquire(this WaitHandle waitHandle) {
            undoAcquire[waitHandle.GetType()](waitHandle);
        }
    }
}
