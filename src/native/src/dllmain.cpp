#include "ClassFactory.h"
#include "sherlock/profiler/profiler.hpp"

using namespace Sherlock;

#if defined(_WIN32)
#define SHERLOCK_EXPORT
#else
#define SHERLOCK_EXPORT __attribute__((visibility("default")))
#endif

BOOL STDMETHODCALLTYPE DllMain(HMODULE, DWORD, LPVOID)
{
    return TRUE;
}

extern "C" SHERLOCK_EXPORT HRESULT STDMETHODCALLTYPE DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
    // {cf0d821e-299b-5307-a3d8-b283c03916dd}
    const GUID CLSID_CorProfiler = { 0xcf0d821e, 0x299b, 0x5307, { 0xa3, 0xd8, 0xb2, 0x83, 0xc0, 0x39, 0x16, 0xdd } };

    if (ppv == nullptr || rclsid != CLSID_CorProfiler)
    {
        return E_FAIL;
    }

    auto factory = new ClassFactory<Profiler>;
    if (factory == nullptr)
    {
        return E_FAIL;
    }

    return factory->QueryInterface(riid, ppv);
}

extern "C" SHERLOCK_EXPORT HRESULT STDMETHODCALLTYPE DllCanUnloadNow()
{
    return S_OK;
}