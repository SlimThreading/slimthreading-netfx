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

namespace SlimThreading {    
    public class ThreadExtensions {
        [ThreadStatic]
        private static ThreadExtensions current;
        private IParkSpotFactory factory;
        
        public static ThreadExtensions ForCurrentThread {
            get { return current ?? (current = new ThreadExtensions()); }
        }

        public IParkSpotFactory ParkSpotFactory {
            get { return factory ?? (factory = EventBasedParkSpotFactory.Current); }
            set { factory = value; }
        }
    }
}