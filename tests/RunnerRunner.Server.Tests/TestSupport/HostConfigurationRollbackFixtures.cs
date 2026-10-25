namespace RunnerRunner.Server.Tests.TestSupport;

internal static class HostConfigurationRollbackFixtures
{
    // Selected nonsecret fields from the read-only, MVCC-consistent export at
    // 2026-10-25T04:20:10.041797Z. Preserve omitted fields and Newtonsoft metadata.
    // No environment, enrollment-token, or other private export fields belong here.
    public const string Json = """
        {
          "captured_at": "2026-10-25T04:20:10.041797+00:00",
          "hosts": {
            "M1ni": {
              "document": {
                "architecture":"Arm64","backendLimitsConfigured":true,"capabilities":["docker","native"],
                "createdAt":"2026-10-01T06:02:26.552794+00:00","displayName":null,"enrolledAt":"2026-10-01T06:04:09.908914Z",
                "groupId":null,"id":"M1ni","isApproved":true,"isDraining":false,"label":"M1ni",
                "labels":{"arch":"arm64","docker":"true","docker_os":"linux","native":"true","os":"macos"},
                "maxDockerContainers":0,"maxNativeProcesses":2,"maxTartVMs":2,"name":"M1ni","platform":1
              },
              "grain_json": {
                "$id":"1","$type":"RunnerRunner.Server.Grains.State.HostGrainState, RunnerRunner.Server",
                "Architecture":"Arm64","BackendLimitsConfigured":true,"CreatedAt":"2026-10-01T06:04:09.9390954Z",
                "Labels":{"$id":"2","$type":"System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[System.String, System.Private.CoreLib]], System.Private.CoreLib","arch":"arm64","docker":"true","docker_os":"linux","native":"true","os":"macos"},
                "MaxNativeProcesses":2,"MaxTartVMs":2,"Name":"M1ni","Platform":1,"Status":2
              }
            },
            "M2ini": {
              "document": {
                "architecture":"Arm64","backendLimitsConfigured":true,"capabilities":["native"],
                "createdAt":"2026-09-20T19:54:15.4643434Z","displayName":null,"enrolledAt":"2026-09-20T19:54:15.4975465Z",
                "groupId":null,"id":"M2ini","isApproved":true,"isDraining":false,"label":"M2ini",
                "labels":{"arch":"arm64","native":"true","os":"macos"},
                "maxDockerContainers":0,"maxNativeProcesses":5,"maxTartVMs":3,"name":"M2ini","platform":1
              },
              "grain_json": {
                "$id":"1","$type":"RunnerRunner.Server.Grains.State.HostGrainState, RunnerRunner.Server",
                "Architecture":"Arm64","BackendLimitsConfigured":true,"CreatedAt":"2026-09-20T19:54:15.5151068Z",
                "Labels":{"$id":"2","$type":"System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[System.String, System.Private.CoreLib]], System.Private.CoreLib","arch":"arm64","native":"true","os":"macos"},
                "MaxNativeProcesses":5,"MaxTartVMs":3,"Name":"M2ini","Platform":1,"Status":2
              }
            },
            "M4ini": {
              "document": {
                "architecture":"Arm64","backendLimitsConfigured":true,"capabilities":["docker","native","tart"],
                "createdAt":"2026-09-20T00:52:31.8965553Z","displayName":null,"enrolledAt":"2026-09-20T00:52:31.9233999Z",
                "groupId":null,"id":"M4ini","isApproved":true,"isDraining":false,"label":"M4ini",
                "labels":{"arch":"arm64","docker":"true","docker_os":"linux","native":"true","os":"macos","tart":"true"},
                "maxDockerContainers":0,"maxNativeProcesses":2,"maxTartVMs":2,"name":"M4ini","platform":1
              },
              "grain_json": {
                "$id":"1","$type":"RunnerRunner.Server.Grains.State.HostGrainState, RunnerRunner.Server",
                "Architecture":"Arm64","BackendLimitsConfigured":true,"CreatedAt":"2026-09-20T00:52:31.9417187Z",
                "Labels":{"$id":"2","$type":"System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[System.String, System.Private.CoreLib]], System.Private.CoreLib","arch":"arm64","docker":"true","docker_os":"linux","native":"true","os":"macos","tart":"true"},
                "MaxNativeProcesses":2,"MaxTartVMs":2,"Name":"M4ini","Platform":1,"Status":2
              }
            },
            "rr-linux": {
              "document": {
                "architecture":"X64","capabilities":["native"],"createdAt":"2026-10-19T21:12:48.476674+00:00",
                "displayName":null,"enrolledAt":"2026-10-19T21:17:59.907251Z","groupId":null,"id":"rr-linux",
                "isApproved":true,"isDraining":false,"label":"rr-linux","labels":{"arch":"x64","native":"true","os":"linux"},
                "maxDockerContainers":0,"maxNativeProcesses":4,"maxTartVMs":0,"name":"rr-linux","platform":0
              },
              "grain_json": {
                "$id":"1","$type":"RunnerRunner.Server.Grains.State.HostGrainState, RunnerRunner.Server",
                "Architecture":"X64","CreatedAt":"2026-10-19T21:17:06.7326862Z",
                "Labels":{"$id":"2","$type":"System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[System.String, System.Private.CoreLib]], System.Private.CoreLib","arch":"x64","native":"true","os":"linux"},
                "MaxNativeProcesses":4,"Name":"rr-linux","Status":2
              }
            },
            "tower-docker": {
              "document": {
                "architecture":"X64","capabilities":["docker","native"],"createdAt":"2026-09-20T00:45:30.9055017Z",
                "displayName":null,"enrolledAt":"2026-09-20T00:45:30.9496898Z","groupId":null,"id":"tower-docker",
                "isApproved":true,"isDraining":false,"label":"tower-docker",
                "labels":{"arch":"x64","docker":"true","docker_os":"linux","native":"true","os":"linux"},
                "maxDockerContainers":10,"maxNativeProcesses":5,"maxTartVMs":0,"name":"tower-docker","platform":0
              },
              "grain_json": {
                "$id":"1","$type":"RunnerRunner.Server.Grains.State.HostGrainState, RunnerRunner.Server",
                "Architecture":"X64","CreatedAt":"2026-09-20T00:45:30.973621Z",
                "Labels":{"$id":"2","$type":"System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[System.String, System.Private.CoreLib]], System.Private.CoreLib","arch":"x64","docker":"true","docker_os":"linux","native":"true","os":"linux"},
                "MaxDockerContainers":10,"MaxNativeProcesses":5,"Name":"tower-docker","Status":2
              }
            }
          }
        }
        """;
}
