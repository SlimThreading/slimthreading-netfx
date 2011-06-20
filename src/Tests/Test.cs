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

namespace Tests {
    static class Test {
        
        public static void Run<T>(int timeout) {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            Action stop = (Action) typeof(T).GetMethod("Run").Invoke(null, new object[0]);
            if (timeout == Timeout.Infinite) {
                VConsole.WriteLine("+++ press any key to terminate...");
                Console.ReadLine();
            } else {
                Thread.Sleep(timeout);
            }

            VConsole.WriteLine("+++ stopping...");
            stop();

            VConsole.Write("+++ press any key to exit...");
            Console.ReadLine();
        }

        static void Main() {
            Run<WaitAnyTest>(10000);
        }
    }
}
