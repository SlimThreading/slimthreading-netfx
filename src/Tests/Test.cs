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

namespace Tests {
    class Test {
        static void Main() {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            Action stop;

            //stop = TestShared.TestBarrier.Run();
            //stop = TestShared.TestBlockingQueue.Run();
            //stop = TestShared.TestCancellationToken.Run();
            //stop = TestShared.TestExchanger.Run();
            //stop = TestShared.TestFairLock.Run();
            //stop = TestShared.TestInitOnceLock.Run();
            //stop = TestShared.TestLock.Run();
            //stop = TestShared.TestMonitorBasedSemaphore.Run();
            //stop = TestShared.TestMonitorBasedLinkedBlockingQueue.Run();
            //stop = TestShared.TestMultipleQueue.Run();
            //stop = TestShared.TestNotificationEvent.Run();
            //stop = TestShared.TestNotificationEventBase.Run();
            //stop = TestShared.TestReadUpgradeWriteLock.Run();
            //stop = TestShared.TestReadWriteLock.Run();
            //stop = TestShared.TestReentrantFairLock.Run();
            //stop = TestShared.TestReentrantLock.Run();
            //stop = TestShared.TestReentrantReadWriteLock.Run();
            //stop = TestShared.TestRegisteredTake.Run();
            //stop = TestShared.TestRegisteredWait.Run();
            //stop = TestShared.TestRendezvousChannel.Run();
            //stop = TestShared.TestSemaphore.Run();
            //stop = TestShared.TestSemaphores.Run();
            //stop = TestShared.TestStreamBlockingQueue.Run();
            //stop = TestShared.TestSynchronizationEvent.Run();
            //stop = TestShared.TestTimer.Run();

            //VConsole.WriteLine("+++ hit <enter> to terminate...");
            //Console.ReadLine();
            //stop();
            //VConsole.Write("+++hit <enter> to exit...");
            //Console.ReadLine();

            //ExchangerTest.Run();
            ExchangerAsyncTest.Run();
        }
    }
}
