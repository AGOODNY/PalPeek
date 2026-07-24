/**
 * @file src/platform/windows/palpeek_audio_activation.h
 * @brief Compatibility declarations for Windows process-loopback activation.
 */
#pragma once

#if __has_include(<audioclientactivationparams.h>)
  #include <audioclientactivationparams.h>
#else
  // MinGW's headers can expose ActivateAudioInterfaceAsync without the
  // Windows 10 build 20348 process-loopback parameter declarations.
  typedef enum AUDIOCLIENT_ACTIVATION_TYPE {
    AUDIOCLIENT_ACTIVATION_TYPE_DEFAULT,
    AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
  } AUDIOCLIENT_ACTIVATION_TYPE;

  typedef enum PROCESS_LOOPBACK_MODE {
    PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
    PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
  } PROCESS_LOOPBACK_MODE;

  typedef struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS {
    DWORD TargetProcessId;
    PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
  } AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS;

  typedef struct AUDIOCLIENT_ACTIVATION_PARAMS {
    AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
    union {
      AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    };
  } AUDIOCLIENT_ACTIVATION_PARAMS;

  #define VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK L"VAD\\Process_Loopback"
#endif
