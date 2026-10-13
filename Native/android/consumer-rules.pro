# Unity calls these methods through JNI; shrinking cannot see the C# references.
-keep class com.vivoderin.joycon2.BleTransport { public *; }
