// NeoBar як неявний шар Vulkan (VK_LAYER_NEOBAR_fps). Завантажувач Vulkan сам підключає його до кожної Vulkan-гри
// (реєстрація в реєстрі робиться з NeoBar), тому API гри відомий точно, а кадри враховуються й обмежуються
// офіційним шляхом: vkQueuePresentKHR. Показ у кадрі тут не робиться - лише облік кадрів, ліміт і підміна повноекрану.
// Вся логіка - у sbfps.cpp (спільна пам'ять із NeoBar, обмежувач); тут лише ланцюжок викликів шару.
#define VK_USE_PLATFORM_WIN32_KHR
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string.h>
#include <vulkan/vulkan.h>
#include <vulkan/vk_layer.h>

extern "C" {
int  SbLayerBdrOn();
void SbLayerAttach();
void SbLayerFrame(void* chain);
void SbLayerStripNote();
void SbLayerSurface(HWND h);
int  SbLayerAcquire();
int  SbLayerRelease();
}

struct InstData
{
    void* key;
    PFN_vkGetInstanceProcAddr gipa;
    PFN_vkDestroyInstance destroy;
    PFN_vkCreateDevice createDev;
    PFN_vkCreateWin32SurfaceKHR createSurf;
    VkInstance inst;
};
struct DevData
{
    void* key;
    PFN_vkGetDeviceProcAddr gdpa;
    PFN_vkDestroyDevice destroy;
    PFN_vkQueuePresentKHR present;
    PFN_vkCreateSwapchainKHR createSc;
    PFN_vkAcquireFullScreenExclusiveModeEXT acquire;
    PFN_vkReleaseFullScreenExclusiveModeEXT release;
};
static InstData insts[8];
static DevData devs[16];
static SRWLOCK lk = SRWLOCK_INIT;

static inline void* Key(const void* h) { return *(void* const*)h; }          // ключ диспетчеризації: перший вказівник об'єкта Vulkan

static InstData* FindInst(void* key)
{
    for (int i = 0; i < 8; i++) if (insts[i].key == key) return &insts[i];
    return nullptr;
}
static DevData* FindDev(void* key)
{
    for (int i = 0; i < 16; i++) if (devs[i].key == key) return &devs[i];
    return nullptr;
}

static VKAPI_ATTR VkResult VKAPI_CALL LCreateSurface(VkInstance inst, const VkWin32SurfaceCreateInfoKHR* ci, const VkAllocationCallbacks* al, VkSurfaceKHR* out)
{
    AcquireSRWLockShared(&lk);
    InstData* d = FindInst(Key(inst));
    PFN_vkCreateWin32SurfaceKHR next = d ? d->createSurf : nullptr;
    ReleaseSRWLockShared(&lk);
    if (!next) return VK_ERROR_INITIALIZATION_FAILED;
    VkResult r = next(inst, ci, al, out);
    if (r == VK_SUCCESS && ci) SbLayerSurface(ci->hwnd);
    return r;
}

static VKAPI_ATTR VkResult VKAPI_CALL LCreateSwapchain(VkDevice dev, const VkSwapchainCreateInfoKHR* ci, const VkAllocationCallbacks* al, VkSwapchainKHR* out)
{
    AcquireSRWLockShared(&lk);
    DevData* d = FindDev(Key(dev));
    PFN_vkCreateSwapchainKHR next = d ? d->createSc : nullptr;
    ReleaseSRWLockShared(&lk);
    if (!next) return VK_ERROR_INITIALIZATION_FAILED;
    if (ci && SbLayerBdrOn())
    {
        // забороняємо грі програмно керований повноекран: вона лишається у вікні, яке NeoBar сам розтягне на монітор
        for (const VkBaseInStructure* h = (const VkBaseInStructure*)ci->pNext; h; h = h->pNext)
            if (h->sType == VK_STRUCTURE_TYPE_SURFACE_FULL_SCREEN_EXCLUSIVE_INFO_EXT)
            {
                VkSurfaceFullScreenExclusiveInfoEXT* f = (VkSurfaceFullScreenExclusiveInfoEXT*)h;
                if (f->fullScreenExclusive != VK_FULL_SCREEN_EXCLUSIVE_DISALLOWED_EXT)
                {
                    f->fullScreenExclusive = VK_FULL_SCREEN_EXCLUSIVE_DISALLOWED_EXT;
                    SbLayerStripNote();
                }
            }
    }
    return next(dev, ci, al, out);
}

static VKAPI_ATTR VkResult VKAPI_CALL LAcquire(VkDevice, VkSwapchainKHR) { return (VkResult)SbLayerAcquire(); }
static VKAPI_ATTR VkResult VKAPI_CALL LRelease(VkDevice, VkSwapchainKHR) { return (VkResult)SbLayerRelease(); }

static VKAPI_ATTR VkResult VKAPI_CALL LQueuePresent(VkQueue q, const VkPresentInfoKHR* pi)
{
    AcquireSRWLockShared(&lk);
    DevData* d = FindDev(Key(q));
    PFN_vkQueuePresentKHR next = d ? d->present : nullptr;
    ReleaseSRWLockShared(&lk);
    if (!next) return VK_ERROR_DEVICE_LOST;
    if (pi && pi->swapchainCount && pi->pSwapchains) SbLayerFrame((void*)(uintptr_t)pi->pSwapchains[0]);
    return next(q, pi);
}

static VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL LGetDeviceProcAddr(VkDevice dev, const char* name)
{
    if (!name) return nullptr;
    if (!strcmp(name, "vkGetDeviceProcAddr")) return (PFN_vkVoidFunction)LGetDeviceProcAddr;
    AcquireSRWLockShared(&lk);
    DevData* d = dev ? FindDev(Key(dev)) : nullptr;
    PFN_vkGetDeviceProcAddr next = d ? d->gdpa : nullptr;
    bool hasPresent = d && d->present, hasSc = d && d->createSc, hasAcq = d && d->acquire, hasRel = d && d->release;
    ReleaseSRWLockShared(&lk);
    if (!next) return nullptr;
    if (!strcmp(name, "vkQueuePresentKHR") && hasPresent) return (PFN_vkVoidFunction)LQueuePresent;
    if (!strcmp(name, "vkCreateSwapchainKHR") && hasSc) return (PFN_vkVoidFunction)LCreateSwapchain;
    if (!strcmp(name, "vkAcquireFullScreenExclusiveModeEXT") && hasAcq) return (PFN_vkVoidFunction)LAcquire;
    if (!strcmp(name, "vkReleaseFullScreenExclusiveModeEXT") && hasRel) return (PFN_vkVoidFunction)LRelease;
    return next(dev, name);
}

static VKAPI_ATTR VkResult VKAPI_CALL LCreateDevice(VkPhysicalDevice phys, const VkDeviceCreateInfo* ci, const VkAllocationCallbacks* al, VkDevice* out)
{
    VkLayerDeviceCreateInfo* chain = (VkLayerDeviceCreateInfo*)ci->pNext;
    while (chain && !(chain->sType == VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO && chain->function == VK_LAYER_LINK_INFO))
        chain = (VkLayerDeviceCreateInfo*)chain->pNext;
    if (!chain) return VK_ERROR_INITIALIZATION_FAILED;
    PFN_vkGetInstanceProcAddr gipa = chain->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    PFN_vkGetDeviceProcAddr gdpa = chain->u.pLayerInfo->pfnNextGetDeviceProcAddr;
    chain->u.pLayerInfo = chain->u.pLayerInfo->pNext;
    PFN_vkCreateDevice next = (PFN_vkCreateDevice)gipa(VK_NULL_HANDLE, "vkCreateDevice");
    if (!next) return VK_ERROR_INITIALIZATION_FAILED;
    VkResult r = next(phys, ci, al, out);
    if (r != VK_SUCCESS) return r;
    VkDevice dev = *out;
    DevData n = {};
    n.key = Key(dev);
    n.gdpa = gdpa;
    n.destroy = (PFN_vkDestroyDevice)gdpa(dev, "vkDestroyDevice");
    n.present = (PFN_vkQueuePresentKHR)gdpa(dev, "vkQueuePresentKHR");
    n.createSc = (PFN_vkCreateSwapchainKHR)gdpa(dev, "vkCreateSwapchainKHR");
    n.acquire = (PFN_vkAcquireFullScreenExclusiveModeEXT)gdpa(dev, "vkAcquireFullScreenExclusiveModeEXT");
    n.release = (PFN_vkReleaseFullScreenExclusiveModeEXT)gdpa(dev, "vkReleaseFullScreenExclusiveModeEXT");
    AcquireSRWLockExclusive(&lk);
    DevData* slot = FindDev(n.key);
    for (int i = 0; i < 16 && !slot; i++) if (!devs[i].key) slot = &devs[i];
    if (slot) *slot = n;
    ReleaseSRWLockExclusive(&lk);
    return VK_SUCCESS;
}

static VKAPI_ATTR void VKAPI_CALL LDestroyDevice(VkDevice dev, const VkAllocationCallbacks* al)
{
    void* key = Key(dev);
    AcquireSRWLockExclusive(&lk);
    DevData* d = FindDev(key);
    PFN_vkDestroyDevice next = d ? d->destroy : nullptr;
    if (d) memset(d, 0, sizeof(*d));
    ReleaseSRWLockExclusive(&lk);
    if (next) next(dev, al);
}

static VKAPI_ATTR VkResult VKAPI_CALL LCreateInstance(const VkInstanceCreateInfo* ci, const VkAllocationCallbacks* al, VkInstance* out)
{
    VkLayerInstanceCreateInfo* chain = (VkLayerInstanceCreateInfo*)ci->pNext;
    while (chain && !(chain->sType == VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO && chain->function == VK_LAYER_LINK_INFO))
        chain = (VkLayerInstanceCreateInfo*)chain->pNext;
    if (!chain) return VK_ERROR_INITIALIZATION_FAILED;
    PFN_vkGetInstanceProcAddr gipa = chain->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    chain->u.pLayerInfo = chain->u.pLayerInfo->pNext;
    PFN_vkCreateInstance next = (PFN_vkCreateInstance)gipa(VK_NULL_HANDLE, "vkCreateInstance");
    if (!next) return VK_ERROR_INITIALIZATION_FAILED;
    VkResult r = next(ci, al, out);
    if (r != VK_SUCCESS) return r;
    VkInstance inst = *out;
    InstData n = {};
    n.key = Key(inst);
    n.inst = inst;
    n.gipa = (PFN_vkGetInstanceProcAddr)gipa(inst, "vkGetInstanceProcAddr");
    n.destroy = (PFN_vkDestroyInstance)gipa(inst, "vkDestroyInstance");
    n.createDev = (PFN_vkCreateDevice)gipa(inst, "vkCreateDevice");
    n.createSurf = (PFN_vkCreateWin32SurfaceKHR)gipa(inst, "vkCreateWin32SurfaceKHR");
    if (!n.gipa) n.gipa = gipa;
    AcquireSRWLockExclusive(&lk);
    InstData* slot = FindInst(n.key);
    for (int i = 0; i < 8 && !slot; i++) if (!insts[i].key) slot = &insts[i];
    if (slot) *slot = n;
    ReleaseSRWLockExclusive(&lk);
    return VK_SUCCESS;
}

static VKAPI_ATTR void VKAPI_CALL LDestroyInstance(VkInstance inst, const VkAllocationCallbacks* al)
{
    void* key = Key(inst);
    AcquireSRWLockExclusive(&lk);
    InstData* d = FindInst(key);
    PFN_vkDestroyInstance next = d ? d->destroy : nullptr;
    if (d) memset(d, 0, sizeof(*d));
    ReleaseSRWLockExclusive(&lk);
    if (next) next(inst, al);
}

static VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL LGetInstanceProcAddr(VkInstance inst, const char* name)
{
    if (!name) return nullptr;
    if (!strcmp(name, "vkGetInstanceProcAddr")) return (PFN_vkVoidFunction)LGetInstanceProcAddr;
    if (!strcmp(name, "vkCreateInstance")) return (PFN_vkVoidFunction)LCreateInstance;
    if (!strcmp(name, "vkGetDeviceProcAddr")) return (PFN_vkVoidFunction)LGetDeviceProcAddr;
    if (!strcmp(name, "vkCreateDevice")) return (PFN_vkVoidFunction)LCreateDevice;
    if (!strcmp(name, "vkDestroyDevice")) return (PFN_vkVoidFunction)LDestroyDevice;
    AcquireSRWLockShared(&lk);
    InstData* d = inst ? FindInst(Key(inst)) : nullptr;
    PFN_vkGetInstanceProcAddr next = d ? d->gipa : nullptr;
    bool hasSurf = d && d->createSurf;
    ReleaseSRWLockShared(&lk);
    if (!strcmp(name, "vkDestroyInstance") && next) return (PFN_vkVoidFunction)LDestroyInstance;
    if (!strcmp(name, "vkCreateWin32SurfaceKHR") && hasSurf) return (PFN_vkVoidFunction)LCreateSurface;
    if (!next) return nullptr;
    return next(inst, name);
}

// точка входу шару: завантажувач питає, який інтерфейс ми підтримуємо
extern "C" __declspec(dllexport) VKAPI_ATTR VkResult VKAPI_CALL vkNegotiateLoaderLayerInterfaceVersion(VkNegotiateLayerInterface* v)
{
    if (!v || v->sType != LAYER_NEGOTIATE_INTERFACE_STRUCT) return VK_ERROR_INITIALIZATION_FAILED;
    if (v->loaderLayerInterfaceVersion >= 2)
    {
        v->pfnGetInstanceProcAddr = LGetInstanceProcAddr;
        v->pfnGetDeviceProcAddr = LGetDeviceProcAddr;
        v->pfnGetPhysicalDeviceProcAddr = nullptr;
    }
    if (v->loaderLayerInterfaceVersion > 2) v->loaderLayerInterfaceVersion = 2;
    SbLayerAttach();
    return VK_SUCCESS;
}
