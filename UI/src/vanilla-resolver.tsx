import { ModuleRegistry } from "cs2/modding";

const registryIndex = {
    Section: ["game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.tsx", "Section"],
    ToolButton: ["game-ui/game/components/tool-options/tool-button/tool-button.tsx", "ToolButton"],
    toolButtonTheme: ["game-ui/game/components/tool-options/tool-button/tool-button.module.scss", "classes"],
    FocusKey: ["game-ui/common/focus/focus-key.ts", "FocusKey"],
};

export class VanillaComponentResolver {
    public static get instance(): VanillaComponentResolver { return this._instance!! }
    private static _instance?: VanillaComponentResolver

    public static setRegistry(in_registry: ModuleRegistry) { this._instance = new VanillaComponentResolver(in_registry); }

    private registryData: ModuleRegistry;
    private cachedData: Partial<Record<keyof typeof registryIndex, any>> = {}

    constructor(in_registry: ModuleRegistry) {
        this.registryData = in_registry;
    }

    private updateCache(entry: keyof typeof registryIndex) {
        const entryData = registryIndex[entry];
        const module = this.registryData.registry.get(entryData[0]);
        if (!module) return null;
        return this.cachedData[entry] = module[entryData[1]];
    }

    public get Section(): any { return this.cachedData["Section"] ?? this.updateCache("Section") ?? "div" }
    public get ToolButton(): any { return this.cachedData["ToolButton"] ?? this.updateCache("ToolButton") ?? "button" }
    public get toolButtonTheme(): any { return this.cachedData["toolButtonTheme"] ?? this.updateCache("toolButtonTheme") ?? {} }

    // Critical for preventing UI crash
    public get FOCUS_DISABLED(): any {
        const FocusKey = this.cachedData["FocusKey"] ?? this.updateCache("FocusKey");
        return FocusKey?.NONE ?? null;
    }
}