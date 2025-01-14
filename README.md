# Java.Interop

**Java.Interop** is a brain-delusional [Second System Syndrome][sss] rebuild
 of the monodroid/Xamarin.Android core, intended to fix some of the shortcomings
  and design mistakes I've made over the years.

[sss]: http://en.wikipedia.org/wiki/Second-system_effect

In particular, it attempts to fix the following issues: 

* Split out the core invocation logic so that the containing assembly is in the
  `xbuild-frameworks\MonoAndroid\v1.0` directory, allowing low-level JNI use
  without taking an API-level constraint.
* Make the assembly a PCL lib.
* Support use of the lib on "desktop" Java VMs. This would allow more testing
  without an Android device, could allow using Xamarin.Android Views to be shown
  in the GUI designer, etc.
* Improve type safety.
* Improve consistency.

In particular are the last two points: Xamarin.Android currently uses `IntPtr`s
*everywhere*, and it's not at all obvious what they are (method IDs vs.
local refs vs. global refs vs. ...). This culminates in `JNIEnv.FindClass()`,
which returns a global reference while most other methods return a local ref.

The `JNIEnv` API is also huge, unwieldy, and terrible.

## Requirements

The current Oracle JDK7 installer only provides 64-bit binaries, while
Mono for OS X is currently a 32-bit binary. These don't work together. :-(

To quickly get a JDK that is usable with the OS X `JavaVM.framework`, run:

    $ make osx-setup

If you're instead a glutton for punishment, or you need to check for
an updated `.pkg` for some future `JavaVM.framework` version:

 1. Go to the [Downloads page](https://developer.apple.com/downloads/index.action).  
    Note: This may require you to login first.
 2. In the "Search" panel (above the "tree" on the left), enter "Java". Hit `[RETURN]`.
 3. Look for the **Java for OS X ... Developer Package** entry.  
    To the right, under the Release Date column, is the installer. Click it.

At the time of this writing, this links to
[Java for OS X 2013-005 Developer Package][osx-jdk6], released October 15, 2013.

[osx-jdk6]: http://adcdownload.apple.com/Developer_Tools/java_for_os_x_2013005_developer_package/java_for_os_x_2013005_dp__11m4609.dmg

Unfortunately, you can't *install* it on El Capitan. It'll install...but it
won't *do* anything, probably because of [System Integrity Protection][sip].

[sip]: https://en.wikipedia.org/wiki/System_Integrity_Protection

To develop on El Capitan, download the above
`java_for_os_x_2013005_dp__11m4609.dmg` file, open it within Finder,
copy the contained `JavaDeveloper.pkg` file into this directory,
then run the `osx-setup` target:

    $ make osx-setup JDK=JavaDeveloper.pkg


## Type Safety

The start of the reboot was to use strongly typed [`SafeHandle`][SafeHandle]
subclasses everywhere instead of `IntPtr`. This allows a local reference to be
type-checked and distinct from a global ref, complete with compiler
type checking.

[SafeHandle]: http://msdn.microsoft.com/en-us/library/system.runtime.interopservices.safehandle.aspx

Since we now have actual types in more places, we can move the current `JNIEnv`
methods into more semantically meaningful types.

Unfortunately, various tests demonstrated that while `SafeHandle`s provided
increased type safety, they did so at a large runtime cost:

1. `SafeHandle`s are reference types, increasing GC heap allocations and pressure.
2. [`SafeHandle`s are *thread-safe* in order to prevent race conditions and handle recycling attacks][reliability].

[reliability]: http://blogs.msdn.com/b/bclteam/archive/2005/03/16/396900.aspx

Compared to a Xamarin.Android-like "use `IntPtr`s for *everything*" binding
approach, the overhead is significant: to *just* invoke
`JNIEnv::CallObjectMethod()`, using `SafeHandle`s for everything causes
execution time to take ~1.4x longer than a comparable struct-oriented approach.

Make the test more realistic -- compared to current Xamarin.Android and
current Java.Interop -- so that `JniEnvironment.Members.CallObjectMethod()`
also calls `JniEnvironment.Errors.ExceptionOccurred()`, which also returns
a JNI local reference -- and runtime execution time *jumped to ~3.6x*:

    # SafeHandle timing: 00:00:09.9393493
    #	Average Invocation: 0.00099393493ms
    # JniObjectReference timing: 00:00:02.7254572
    #	Average Invocation: 0.00027254572ms

(See the [tests/invocation-overhead](tests/invocation-overhead) directory
for the invocation comparison sourcecode.)

*This is not acceptable*. Performance is a concern with Xamarin.Android;
we can't be making it *worse*.

Meanwhile, I *really* dislike using `IntPtr`s everywhere, as it doesn't let you
know what the value actually represents.

To solve this issue, *avoid `SafeHandle` types* in the public API.

Downside: this means we can't have the GC collect our garbage JNI references.

Upside: the Java.Interop effort will actually be usable.

Instead of using `SafeHandle` types, we introduce a
`JniObjectReference` struct type. This represents a JNI Local, Global, or
WeakGlobal object reference. The `JniObjectReference` struct also contains
the *reference type* as `JniObjectReferenceType`.
`jmethodID` and `jfieldID` become "normal" class types, permitting type safety,
but lose their `SafeHandle` status, which was never really necessary because
they don't require cleanup *anyway*. Furthermore, these values should be
*cached* -- see `JniPeerMembers` -- so making them GC objects shouldn't be
a long-term problem.

By doing so, we allow Java.Interop to have *two separate implementations*,
controlled by build-time `#define`s:

* `FEATURE_HANDLES_ARE_SAFE_HANDLES`: Causes `JniObjectReference` to
    contain a `SafeHandle` wrapping the underlying JNI handle.
* `FEATURE_HANDLES_ARE_INTPTRS`: Causes `JniObjectReference` to contain
    an `IntPtr` for the underlying JNI handle.

The rationale for this is twofold:

1. It allows swapping out "safer" `SafeHandle` and "less safe" `IntPtr`
    implementations, permitting easier performance comparisons.
2. It allows migrating the existing code, as some of the existing
    tests may assume that JNI handles are garbage collected, which
    won't be the case when `FEATURE_HANDLES_ARE_INTPTRS` is set.


## Naming Conventions

Types with a `Java` prefix are "high-level" types which participate in cross-VM
object-reference semantics, e.g. you could add a `JavaObject` subclass to a
Java-side collection, perform a GC, and the instance will survive the GC.

Types with a `Jni` prefix are "low-level" types and do *not* participate in
object-reference semantics.

## Android Tests

[src/Android.Interop](src/Android.Interop) is vestigial; it was for
testing before a subset of Java.Interop was integrated with
Xamarin.Android 6.1 ("cycle 7"). It should arguably be deleted.

The top-level `make run-android` target will run the Java.Interop unit tests
on Android via the Android.Interop-Tests project.

The Android.Interop-Tests project currently contains *all* tests, including
the time intensive "PerformanceTests".

To run a specific test fixture, set the FIXTURE variable:

    make run-android FIXTURE=Java.Interop.PerformanceTests.TimingTests

## Notes

### JDK and Global References

The JDK VM supports an effectively unlimited number of global references.
While Dalvik bails out after creating ~64k GREFs, consider the following
on the JDK:

    var t = new JniType ("java/lang/Object");
    var c = t.GetConstructor ("()V");
    var o = t.NewInstance (c);
    int count = 0;
    while (true) {
        Console.WriteLine ("count: {0}", count++);
        o.NewGlobalRef ();
    }

I halted the above loop after reaching 25686556 instances.

    count: 25686556
    ^C

I'm not sure when the JDK would stop handing out references, but it's probably
bound to process heap limits (e.g. depends on 32-bit vs. 64-bit process).

