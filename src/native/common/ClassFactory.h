// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#pragma once

#define NOMINMAX

#include "profilercommon.h"
#include <atomic>

template
<class T>
class ClassFactory : public IClassFactory
{
private:
    std::atomic<int> refCount;
public:

    ClassFactory() : refCount(0)
    {
    }

    ~ClassFactory()
    {
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void **ppvObject)
    {
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppvObject = this;
            this->AddRef();
            return S_OK;
        }

        *ppvObject = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef()
    {
        return std::atomic_fetch_add(&this->refCount, 1) + 1;
    }

    ULONG STDMETHODCALLTYPE Release()
    {
        ULONG count = static_cast<ULONG>(std::atomic_fetch_sub(&this->refCount, 1) - 1);
        if (count == 0)
        {
            delete this;
        }

        return count;
    }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown *pUnkOuter, REFIID riid, void **ppvObject)
    {
        if (ppvObject == nullptr)
        {
            return E_POINTER;
        }
        *ppvObject = nullptr;
        if (pUnkOuter != nullptr)
        {
            return E_FAIL;
        }

        T* profiler = nullptr;
        try
        {
            profiler = new T();
        }
        catch (...)
        {
            return E_OUTOFMEMORY; // no exception may cross the COM boundary
        }

        // Hold a reference across QueryInterface, so a failed one frees the object instead of leaking it.
        profiler->AddRef();
        HRESULT hr = profiler->QueryInterface(riid, ppvObject);
        profiler->Release();
        return hr;
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL fLock)
    {
        return S_OK;
    }
};
