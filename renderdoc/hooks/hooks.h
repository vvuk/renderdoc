/******************************************************************************
 * The MIT License (MIT)
 * 
 * Copyright (c) 2014 Crytek
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 ******************************************************************************/



#pragma once

#include <map>
using std::map;

#include "os/os_specific.h"

// include os-specific hooking mechanisms here

#if defined(WIN32)

#include "3rdparty/mhook/mhook-lib/mhook.h"

template<typename FuncType>
class Hook
{
	public:
		Hook() { trampoline = NULL; }
		~Hook() { Mhook_Unhook(&trampoline); }
		
		FuncType operator()()
		{
			return (FuncType)trampoline;
		}

		bool Initialize(const char *function, const char *module_name, void *destination_function_ptr)
		{
			trampoline = Process::GetFunctionAddress(module_name, function);

			if(trampoline == NULL)
				return false;

			return Mhook_SetHook(&trampoline, destination_function_ptr);
		}

	private:
		void* trampoline;
};

#define HOOKS_BEGIN() Mhook_SuspendOtherThreads()
#define HOOKS_END() Mhook_ResumeOtherThreads()

#elif defined(LINUX)

// just need this for dlsym
#include <dlfcn.h>

#define HOOKS_BEGIN()
#define HOOKS_END()

#else

#error "undefined platform"

#endif

// defines the interface that a library hooking class will implement.
// the libName is the name they used when registering
struct LibraryHook
{
	virtual bool CreateHooks(const char *libName) = 0;
	virtual void EnableHooks(const char *libName, bool enable) = 0;
};

// this singleton allows you to compile in code that defines a hook for a given library
// (and it will be registered). Then when the renderdoc library is initialised in the target
// program CreateHooks() will be called to set up the hooks.
class LibraryHooks
{
	public:
		static LibraryHooks &GetInstance();
		void RegisterHook(const char *libName, LibraryHook *hook);
		void CreateHooks();
		void EnableHooks(bool enable);

	private:
		typedef map<const char *, LibraryHook *> HookMap;

		HookMap m_Hooks;
};