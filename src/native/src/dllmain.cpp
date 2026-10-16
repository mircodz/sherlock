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

    ClassFactory<Profiler>* factory = nullptr;
    try
    {
        factory = new ClassFactory<Profiler>;
    }
    catch (...)
    {
        return E_OUTOFMEMORY; // no exception may cross the COM boundary
    }

    // Hold a reference across QueryInterface, so a failed one frees the factory instead of leaking it.
    factory->AddRef();
    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

extern "C" SHERLOCK_EXPORT HRESULT STDMETHODCALLTYPE DllCanUnloadNow()
{
    return S_OK;
}