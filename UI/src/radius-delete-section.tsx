import React, { useEffect } from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import { tool } from "cs2/bindings";

// Note: We are NOT importing VanillaComponentResolver yet to isolate the crash.

const toolState$ = bindValue<number>("RadiusDelete", "ToolState");
const radius$ = bindValue<number>("RadiusDelete", "Radius");
const filters$ = bindValue<number>("RadiusDelete", "Filters");

export const RadiusDeleteSection: any = (Component: any) => (props: any) => {
    const toolState = useValue(toolState$);
    const radius = useValue(radius$);
    const filters = useValue(filters$);
    const nativeActiveTool = useValue(tool.activeTool$);

    const isBulldozerActive = nativeActiveTool.id === "Bulldoze Tool" || toolState > 0;

    const toggleTool = () => trigger("RadiusDelete", "ToggleTool");
    const setRadius = (x: number) => trigger("RadiusDelete", "SetRadius", x);

    // DEBUG LOG: Watch the state changes in the console
    useEffect(() => {
        if (isBulldozerActive) {
            console.log(`[RadiusDelete] State: ${toolState}, Active: ${isBulldozerActive}`);
        }
    }, [toolState, isBulldozerActive]);

    const result = Component(props);

    if (isBulldozerActive) {
        if (!result.props.children) result.props.children = [];

        // SAFE MODE UI: Standard HTML elements only. 
        // This cannot crash due to missing Game Assets or FocusKeys.
        const mySections = (
            <div key="radius-delete-safe-ui" style={{
                padding: "10px",
                backgroundColor: "rgba(0,0,0,0.8)",
                border: "1px solid red",
                color: "white",
                marginTop: "10px",
                pointerEvents: "auto" // Ensure we can click it
            }}>
                <div style={{ fontWeight: "bold", marginBottom: "5px" }}>Radius Tool (Debug)</div>

                <button
                    onClick={toggleTool}
                    style={{
                        background: toolState === 2 ? "green" : "grey",
                        color: "white",
                        padding: "5px",
                        marginBottom: "10px",
                        width: "100%"
                    }}
                >
                    {toolState === 2 ? "ENABLED" : "ENABLE RADIUS"}
                </button>

                {toolState === 2 && (
                    <div>
                        <div style={{ marginBottom: "5px" }}>Radius: {Math.round(radius || 20)}m</div>
                        <input
                            type="range"
                            min={5} max={200} step={5}
                            value={radius || 20}
                            onChange={(e) => setRadius(parseFloat(e.target.value))}
                            style={{ width: "100%" }}
                        />
                    </div>
                )}
            </div>
        );

        if (Array.isArray(result.props.children)) {
            result.props.children.push(mySections);
        } else {
            result.props.children = [result.props.children, mySections];
        }
    }

    return result;
};