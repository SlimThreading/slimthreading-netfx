using System;

namespace Tests {
    public class Assert {
        public class NullException : Exception {
            
            public NullException(object actual)
                : base(string.Format("Assert.IsNull() failed: actual was {0}", actual)) { }
        }

        public class EqualException : Exception {

            public EqualException(object expected, object actual)
                : base(string.Format("Assert.AreEqual() failed: expected {0}, actual was {1}", 
                                     expected, actual)) { }
        }

        public static void AreEqual<T>(T expected, T actual) {
            if (!Equals(expected, actual)) {
                throw new EqualException(expected, actual);
            }
        }

        public static void IsNull(object @object) {
            if (@object != null) {
                throw new NullException(@object);
            }
        }

        private static bool Equals<T>(T x, T y) {
            Type type = typeof(T);

            if (!type.IsValueType || 
                (type.IsGenericType && type.GetGenericTypeDefinition().IsAssignableFrom(typeof(Nullable<>)))) {
                    
                    if (Object.Equals(x, default(T))) { 
                        return Object.Equals(y, default(T));
                    }

                    if (Object.Equals(y, default(T))) {
                        return false;
                    }
                }

            if (x.GetType() != y.GetType()) {
                return false;
            }

            var equatable = x as IEquatable<T>;
            if (equatable != null) { 
                return equatable.Equals(y);
            }

            var comparableGen = x as IComparable<T>;
            if (comparableGen != null) { 
                return comparableGen.CompareTo(y) == 0;
            }

            var comparable2 = x as IComparable;
            if (comparable2 != null) {
                return comparable2.CompareTo(y) == 0;
            }
                
            return Object.Equals(x, y);
        }
    }
}