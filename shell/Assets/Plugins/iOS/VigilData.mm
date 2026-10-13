// Where the Unity player loads its data from.
//
// The IPA only contains the player (engine + compiled game code + its own small Data/ folder). The converted
// game data (globalgamemanagers, level*, sharedassets*, resources.assets, Resources/, StreamingAssets/ -
// several GB) is copied by the user into Documents/VigilData with Filza / Files.
//
// Unity asks UnityDataBundleDir() (Classes/Unity/Filesystem.mm, patched by IosShellBuild to call
// VigilResolveDataBundleDir) for the folder that contains "Data". Here a Library/VigilRuntime/Data folder is
// rebuilt at every launch out of symbolic links:
//   * from the app bundle's Data: Managed/ (IL2CPP metadata), unity default resources, boot.config and the
//     *.json files - they must match the player binary;
//   * from Documents/VigilData: everything else (the game's scenes, assets, Resources/), and
//     StreamingAssets/ as Data/Raw (FMOD banks, probe volume data).
// If the game data is missing the player starts on the shell's own empty scene and the iOS mod explains
// what to copy where.
#import <Foundation/Foundation.h>
#include <string.h>

static char gVigilDataStatus[2048] = "not resolved yet";

static NSString *VigilFindContent(NSString *docs)
{
    NSFileManager *fm = [NSFileManager defaultManager];
    // tolerate an extra folder level from unzipping / copying (VigilData/VigilData, VigilData/Vigil_Data, ...)
    NSArray<NSString *> *candidates = @[
        @"VigilData", @"VigilData/VigilData", @"VigilData/Vigil_Data", @"VigilData/Data", @"Vigil_Data", @"VigilData/VigilData/Data",
    ];
    for (NSString *c in candidates)
    {
        NSString *p = [docs stringByAppendingPathComponent:c];
        if ([fm fileExistsAtPath:[p stringByAppendingPathComponent:@"globalgamemanagers"]]) return p;
    }
    // anything else up to two levels deep ("VigilData 2" from the Files app when VigilData already exists, ...)
    for (NSString *a in [[fm contentsOfDirectoryAtPath:docs error:nil] sortedArrayUsingSelector:@selector(compare:)])
    {
        NSString *pa = [docs stringByAppendingPathComponent:a];
        if ([a hasPrefix:@"."] || [a isEqualToString:@"Inbox"]) continue;
        if ([fm fileExistsAtPath:[pa stringByAppendingPathComponent:@"globalgamemanagers"]]) return pa;
        for (NSString *b in [fm contentsOfDirectoryAtPath:pa error:nil])
        {
            NSString *pb = [pa stringByAppendingPathComponent:b];
            if ([fm fileExistsAtPath:[pb stringByAppendingPathComponent:@"globalgamemanagers"]]) return pb;
        }
    }
    return nil;
}

static BOOL VigilShellWins(NSString *name)
{
    return [name isEqualToString:@"Managed"] || [name isEqualToString:@"unity default resources"] ||
           [name isEqualToString:@"boot.config"] || [name hasSuffix:@".json"] || [name isEqualToString:@"Il2CppData"];
}

extern "C" NSString *VigilResolveDataBundleDir(NSString *bundleDir)
{
    @autoreleasepool
    {
        NSFileManager *fm = [NSFileManager defaultManager];
        NSString *docs = NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES)[0];
        NSString *lib = NSSearchPathForDirectoriesInDomains(NSLibraryDirectory, NSUserDomainMask, YES)[0];
        NSString *content = VigilFindContent(docs);
        if (content == nil)
        {
            [fm createDirectoryAtPath:[docs stringByAppendingPathComponent:@"VigilData"] withIntermediateDirectories:YES attributes:nil error:nil];
            snprintf(gVigilDataStatus, sizeof(gVigilDataStatus), "MISSING: no globalgamemanagers in Documents/VigilData - using the app's built-in data");
            NSLog(@"[Vigil-iOS] %s", gVigilDataStatus);
            return bundleDir;
        }

        NSString *runtime = [lib stringByAppendingPathComponent:@"VigilRuntime"];
        NSString *data = [runtime stringByAppendingPathComponent:@"Data"];
        NSString *shellData = [bundleDir stringByAppendingPathComponent:@"Data"];
        NSError *err = nil;
        [fm removeItemAtPath:runtime error:nil];
        if (![fm createDirectoryAtPath:data withIntermediateDirectories:YES attributes:nil error:&err])
        {
            snprintf(gVigilDataStatus, sizeof(gVigilDataStatus), "ERROR: cannot create %s: %s - using the app's built-in data",
                     data.UTF8String, err.localizedDescription.UTF8String ?: "?");
            NSLog(@"[Vigil-iOS] %s", gVigilDataStatus);
            return bundleDir;
        }

        int fromShell = 0, fromContent = 0, failed = 0;
        NSMutableSet<NSString *> *done = [NSMutableSet set];
        // 1. the player's own files
        for (NSString *name in [fm contentsOfDirectoryAtPath:shellData error:nil])
        {
            if (!VigilShellWins(name)) continue;
            if ([fm createSymbolicLinkAtPath:[data stringByAppendingPathComponent:name] withDestinationPath:[shellData stringByAppendingPathComponent:name] error:nil]) fromShell++;
            else failed++;
            [done addObject:name];
        }
        // 2. the game's data
        for (NSString *name in [fm contentsOfDirectoryAtPath:content error:nil])
        {
            if ([done containsObject:name] || [name hasPrefix:@"."]) continue;
            NSString *dst = [name isEqualToString:@"StreamingAssets"] ? @"Raw" : name;
            if ([done containsObject:dst]) continue;
            if ([fm createSymbolicLinkAtPath:[data stringByAppendingPathComponent:dst] withDestinationPath:[content stringByAppendingPathComponent:name] error:nil]) fromContent++;
            else failed++;
            [done addObject:dst];
        }
        // 3. anything else the player brought that the game data does not replace
        for (NSString *name in [fm contentsOfDirectoryAtPath:shellData error:nil])
        {
            if ([done containsObject:name]) continue;
            if ([fm createSymbolicLinkAtPath:[data stringByAppendingPathComponent:name] withDestinationPath:[shellData stringByAppendingPathComponent:name] error:nil]) fromShell++;
            else failed++;
            [done addObject:name];
        }
        snprintf(gVigilDataStatus, sizeof(gVigilDataStatus), "OK: game data from %s (%d entries), player files %d, failed links %d",
                 content.UTF8String, fromContent, fromShell, failed);
        NSLog(@"[Vigil-iOS] %s", gVigilDataStatus);
        return runtime;
    }
}

extern "C" const char *VigilData_Status()
{
    return gVigilDataStatus;
}
