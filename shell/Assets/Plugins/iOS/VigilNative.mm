// VIGIL iOS native helpers, called from VigilShim (C#) through DllImport("__Internal").
//
//  * VigilFace_*  ARKit face tracking on the TrueDepth camera: eyeBlinkLeft / eyeBlinkRight blend shapes.
//                 No camera image is rendered or kept; only the two blink coefficients are read.
//  * VigilPad_*   game controllers (MFi / Xbox / PlayStation) through the GameController framework.
//  * VigilFmod_*  registers the statically linked Steam Audio FMOD plugin DSPs with FMOD (iOS cannot
//                 load plugin libraries at runtime).
#import <Foundation/Foundation.h>
#import <ARKit/ARKit.h>
#import <AVFoundation/AVFoundation.h>
#import <GameController/GameController.h>
#include <string.h>

// ======================================================================================================
// Face tracking
// ======================================================================================================

// Must match VigilShim.FaceBlink.NativeState
enum { kOff = 0, kStarting = 1, kTracking = 2, kNoFace = 3, kFailed = -1, kUnsupported = -2 };

@interface VigilFaceTracker : NSObject <ARSessionDelegate>
@property (nonatomic, strong) ARSession *session;
@property (atomic) float left, right;
@property (atomic) int state;
@property (atomic) double startTime, lastFaceTime;
@property (atomic, copy) NSString *error;
@end

static VigilFaceTracker *gTracker;
static char gFaceError[512];

@implementation VigilFaceTracker

- (void)fail:(NSString *)why
{
    self.error = why;
    self.state = kFailed;
    NSLog(@"[Vigil-iOS] face tracking: %@", why);
}

- (void)start
{
    if (![ARFaceTrackingConfiguration isSupported]) { self.state = kUnsupported; self.error = @"ARKit face tracking is not supported on this device"; return; }

    AVAuthorizationStatus auth = [AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeVideo];
    if (auth == AVAuthorizationStatusDenied || auth == AVAuthorizationStatusRestricted)
    {
        [self fail:@"camera access is denied - allow it in Settings > VIGIL > Camera"];
        return;
    }

    ARFaceTrackingConfiguration *config = [ARFaceTrackingConfiguration new];
    config.lightEstimationEnabled = NO;
    if (@available(iOS 13.0, *)) config.maximumNumberOfTrackedFaces = 1;
    // the lowest frame rate format is plenty for blinks and saves power for the game
    ARVideoFormat *best = nil;
    for (ARVideoFormat *f in ARFaceTrackingConfiguration.supportedVideoFormats)
        if (best == nil || f.framesPerSecond < best.framesPerSecond ||
            (f.framesPerSecond == best.framesPerSecond && f.imageResolution.width < best.imageResolution.width))
            best = f;
    if (best != nil && best.framesPerSecond >= 30) config.videoFormat = best;

    if (self.session == nil)
    {
        self.session = [ARSession new];
        self.session.delegate = self;
        self.session.delegateQueue = dispatch_queue_create("vigil.face", DISPATCH_QUEUE_SERIAL);
    }
    self.left = self.right = -1;
    self.state = kStarting;
    self.startTime = CACurrentMediaTime();
    self.lastFaceTime = 0;
    [self.session runWithConfiguration:config options:ARSessionRunOptionResetTracking | ARSessionRunOptionRemoveExistingAnchors];
    NSLog(@"[Vigil-iOS] face tracking started (%@ @ %ld fps)", best != nil ? NSStringFromCGSize(best.imageResolution) : @"default", (long)(best != nil ? best.framesPerSecond : 0));
}

- (void)stop
{
    [self.session pause];
    self.state = kOff;
    self.left = self.right = -1;
}

- (void)session:(ARSession *)session didUpdateAnchors:(NSArray<__kindof ARAnchor *> *)anchors
{
    for (ARAnchor *a in anchors)
    {
        if (![a isKindOfClass:[ARFaceAnchor class]]) continue;
        ARFaceAnchor *face = (ARFaceAnchor *)a;
        if (!face.isTracked) { self.state = kNoFace; continue; }
        NSDictionary<ARBlendShapeLocation, NSNumber *> *bs = face.blendShapes;
        self.left = bs[ARBlendShapeLocationEyeBlinkLeft].floatValue;
        self.right = bs[ARBlendShapeLocationEyeBlinkRight].floatValue;
        self.lastFaceTime = CACurrentMediaTime();
        self.state = kTracking;
    }
}

- (void)session:(ARSession *)session didRemoveAnchors:(NSArray<__kindof ARAnchor *> *)anchors
{
    for (ARAnchor *a in anchors)
        if ([a isKindOfClass:[ARFaceAnchor class]]) self.state = kNoFace;
}

- (void)session:(ARSession *)session didFailWithError:(NSError *)error
{
    NSString *why = error.localizedDescription ?: @"unknown ARKit error";
    if (error.code == ARErrorCodeCameraUnauthorized) why = @"camera access is denied - allow it in Settings > VIGIL > Camera";
    [self fail:why];
}

- (void)sessionWasInterrupted:(ARSession *)session
{
    if (self.state != kFailed) self.state = kNoFace;
    NSLog(@"[Vigil-iOS] face tracking interrupted");
}

- (void)sessionInterruptionEnded:(ARSession *)session
{
    NSLog(@"[Vigil-iOS] face tracking interruption ended");
}

@end

extern "C" int VigilFace_IsSupported()
{
    return [ARFaceTrackingConfiguration isSupported] ? 1 : 0;
}

extern "C" int VigilFace_Start()
{
    __block int result = 0;
    void (^work)(void) = ^{
        if (gTracker == nil) gTracker = [VigilFaceTracker new];
        [gTracker start];
        result = gTracker.state == kStarting ? 0 : 1;
    };
    if ([NSThread isMainThread]) work(); else dispatch_sync(dispatch_get_main_queue(), work);
    return result;
}

extern "C" void VigilFace_Stop()
{
    void (^work)(void) = ^{ [gTracker stop]; };
    if ([NSThread isMainThread]) work(); else dispatch_sync(dispatch_get_main_queue(), work);
}

extern "C" int VigilFace_Poll(float *left, float *right)
{
    *left = *right = -1;
    if (gTracker == nil) return kOff;
    int s = gTracker.state;
    double now = CACurrentMediaTime();
    // no face anchor yet, or none updated for a while: "no face" (PBP sends -1 in that case)
    if (s == kStarting && now - gTracker.startTime > 2.0) s = kNoFace;
    if (s == kTracking && now - gTracker.lastFaceTime > 0.5) s = kNoFace;
    if (s == kTracking) { *left = gTracker.left; *right = gTracker.right; }
    return s;
}

extern "C" const char *VigilFace_LastError()
{
    NSString *e = gTracker.error ?: @"";
    strlcpy(gFaceError, e.UTF8String ?: "", sizeof(gFaceError));
    return gFaceError;
}

// ======================================================================================================
// Game controllers
// ======================================================================================================

extern "C" int VigilPad_Poll(float *axes, int *buttons)
{
    *buttons = 0;
    for (int i = 0; i < 6; i++) axes[i] = 0;
    GCController *c = nil;
    if (@available(iOS 14.0, *)) c = GCController.current;
    if (c == nil) c = GCController.controllers.firstObject;
    GCExtendedGamepad *g = c.extendedGamepad;
    if (g == nil) return 0;
    axes[0] = g.leftThumbstick.xAxis.value;
    axes[1] = g.leftThumbstick.yAxis.value;
    axes[2] = g.rightThumbstick.xAxis.value;
    axes[3] = g.rightThumbstick.yAxis.value;
    axes[4] = g.leftTrigger.value;
    axes[5] = g.rightTrigger.value;
    int b = 0;
    if (g.buttonA.pressed) b |= 1;
    if (g.buttonB.pressed) b |= 2;
    if (g.buttonX.pressed) b |= 4;
    if (g.buttonY.pressed) b |= 8;
    if (g.leftShoulder.pressed) b |= 16;
    if (g.rightShoulder.pressed) b |= 32;
    if (g.leftTrigger.pressed) b |= 64;
    if (g.rightTrigger.pressed) b |= 128;
    if (g.buttonMenu.pressed) b |= 256;
    if (g.buttonOptions != nil && g.buttonOptions.pressed) b |= 512;
    if (g.leftThumbstickButton != nil && g.leftThumbstickButton.pressed) b |= 1024;
    if (g.rightThumbstickButton != nil && g.rightThumbstickButton.pressed) b |= 2048;
    if (g.dpad.up.pressed) b |= 4096;
    if (g.dpad.down.pressed) b |= 8192;
    if (g.dpad.left.pressed) b |= 16384;
    if (g.dpad.right.pressed) b |= 32768;
    *buttons = b;
    return 1;
}

// ======================================================================================================
// Crash logging: a fatal signal appends "FATAL SIGNAL" + a native backtrace to Documents/Vigil-iOS.log,
// then the previous handler (Unity's crash reporter / the default) runs.
// ======================================================================================================
#include <signal.h>
#include <execinfo.h>
#include <fcntl.h>
#include <unistd.h>

static char gCrashLogPath[1024];
static struct sigaction gPrevHandlers[NSIG];
static volatile sig_atomic_t gInCrash;

static void VigilWrite(int fd, const char *s) { if (s) write(fd, s, strlen(s)); }

static void VigilCrashHandler(int sig, siginfo_t *info, void *ctx)
{
    if (!gInCrash)
    {
        gInCrash = 1;
        int fd = open(gCrashLogPath, O_WRONLY | O_APPEND | O_CREAT, 0644);
        if (fd >= 0)
        {
            char buf[128];
            snprintf(buf, sizeof(buf), "\n=== FATAL SIGNAL %d (%s) at address %p ===\nnative backtrace:\n", sig, sys_signame[sig], info ? info->si_addr : NULL);
            VigilWrite(fd, buf);
            void *frames[64];
            int n = backtrace(frames, 64);
            backtrace_symbols_fd(frames, n, fd);
            VigilWrite(fd, "=== end of crash report ===\n");
            close(fd);
        }
    }
    struct sigaction *prev = &gPrevHandlers[sig];
    if (prev->sa_flags & SA_SIGINFO) { if (prev->sa_sigaction) { prev->sa_sigaction(sig, info, ctx); return; } }
    else if (prev->sa_handler != SIG_DFL && prev->sa_handler != SIG_IGN && prev->sa_handler != NULL) { prev->sa_handler(sig); return; }
    signal(sig, SIG_DFL);
    raise(sig);
}

extern "C" void VigilCrash_Install(const char *logPath)
{
    if (logPath == NULL) return;
    strlcpy(gCrashLogPath, logPath, sizeof(gCrashLogPath));
    int sigs[] = { SIGSEGV, SIGBUS, SIGILL, SIGFPE, SIGABRT, SIGTRAP };
    for (int s : sigs)
    {
        struct sigaction sa;
        memset(&sa, 0, sizeof(sa));
        sa.sa_sigaction = VigilCrashHandler;
        sa.sa_flags = SA_SIGINFO | SA_ONSTACK;
        sigemptyset(&sa.sa_mask);
        sigaction(s, &sa, &gPrevHandlers[s]);
    }
    NSLog(@"[Vigil-iOS] crash logger installed -> %s", logPath);
}

// ======================================================================================================
// FMOD static plugins (Steam Audio)
// ======================================================================================================

// From libfmodstudiounityplugin.a / libphonon_fmod.a (declared here so no SDK headers are needed).
extern "C" int FMOD5_System_RegisterDSP(void *system, const void *description, unsigned int *handle);
extern "C" void *FMOD_SteamAudio_Spatialize_GetDSPDescription();
extern "C" void *FMOD_SteamAudio_MixerReturn_GetDSPDescription();
extern "C" void *FMOD_SteamAudio_Reverb_GetDSPDescription();

static char gFmodMsg[1024];

extern "C" int VigilFmod_RegisterStaticPlugins(void *coreSystem)
{
    struct { const char *name; void *(*get)(); } plugins[] = {
        { "Steam Audio Spatializer", FMOD_SteamAudio_Spatialize_GetDSPDescription },
        { "Steam Audio Mixer Return", FMOD_SteamAudio_MixerReturn_GetDSPDescription },
        { "Steam Audio Reverb", FMOD_SteamAudio_Reverb_GetDSPDescription },
    };
    gFmodMsg[0] = 0;
    int ok = 0;
    for (auto &p : plugins)
    {
        unsigned int handle = 0;
        void *desc = p.get();
        int r = desc != NULL ? FMOD5_System_RegisterDSP(coreSystem, desc, &handle) : -1;
        char line[160];
        snprintf(line, sizeof(line), "%s%s=%s", gFmodMsg[0] ? ", " : "", p.name, r == 0 ? "ok" : (desc == NULL ? "no description" : "error"));
        if (r != 0 && desc != NULL) snprintf(line, sizeof(line), "%s%s=FMOD error %d", gFmodMsg[0] ? ", " : "", p.name, r);
        strlcat(gFmodMsg, line, sizeof(gFmodMsg));
        if (r == 0) ok++;
    }
    return ok;
}

extern "C" const char *VigilFmod_LastMessage()
{
    return gFmodMsg;
}
