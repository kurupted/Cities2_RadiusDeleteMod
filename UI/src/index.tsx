import { ModRegistrar } from "cs2/modding";
import { RadiusDeleteSection } from "./radius-delete-section";
import { VanillaComponentResolver } from "./vanilla-resolver";

const register: ModRegistrar = (moduleRegistry) => {
    // 1. Init Resolver
    VanillaComponentResolver.setRegistry(moduleRegistry);

    // 2. Inject
    moduleRegistry.extend(
        "game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.tsx",
        'MouseToolOptions',
        RadiusDeleteSection
    );
}

export default register;