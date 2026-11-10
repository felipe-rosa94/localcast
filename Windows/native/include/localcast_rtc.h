#pragma once

#ifdef _WIN32
#ifdef LOCALCAST_EXPORTS
#define LOCALCAST_API __declspec(dllexport)
#else
#define LOCALCAST_API __declspec(dllimport)
#endif
#else
#define LOCALCAST_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

LOCALCAST_API int lc_abi_version(void);

#ifdef __cplusplus
}
#endif
