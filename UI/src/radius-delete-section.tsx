import React from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import { tool } from "cs2/bindings";
import { VanillaComponentResolver } from "./vanilla-resolver";
import styles from "./radius-delete.module.scss";
import radiusIcon from "./img/RadiusDelete.svg";

const arrowDownSrc = "coui://uil/Standard/ArrowDownThickStroke.svg";
const arrowUpSrc = "coui://uil/Standard/ArrowUpThickStroke.svg";
const treeIconSrc = "coui://uil/Standard/TreeAdult.svg";

const toolState$ = bindValue<number>("RadiusDelete", "ToolState");
const radius$ = bindValue<number>("RadiusDelete", "Radius");
const filters$ = bindValue<number>("RadiusDelete", "Filters");

export const RadiusDeleteSection: any = (Component: any) => (props: any) => {
    const toolState = useValue(toolState$);
    const radius = useValue(radius$);
    const filters = useValue(filters$);
    const nativeActiveTool = useValue(tool.activeTool$);

    const isOurToolActive = toolState === 2;
    const isBulldozerPanelOpen = nativeActiveTool.id === "Bulldoze Tool";

    const _forceBundle = radiusIcon;

    const setRadius = (x: number) => {
        const val = isNaN(x) ? 30 : Math.max(1, Math.min(1000, x));
        trigger("RadiusDelete", "SetRadius", val);
    };

    const result = Component(props);

    if (isBulldozerPanelOpen) {
        const { Section, ToolButton, toolButtonTheme, mouseToolOptionsTheme, FOCUS_DISABLED } = VanillaComponentResolver.instance;
        const iconPath = "coui://ui-mods/images/RadiusDelete.svg";

        const myUI = (
            <div key="radius-delete-ui" className={styles.container}>
                <Section title="Radius Mode Active">
                    <ToolButton
                        className={toolButtonTheme.button}
                        selected={isOurToolActive}
                        src={iconPath}
                        onSelect={() => trigger("RadiusDelete", "ToggleTool")}
                        tooltip="Toggle Radius Delete Mode"
                        focusKey={FOCUS_DISABLED}
                    />
                </Section>

                <Section title="Brush Size">
                    <ToolButton
                        className={mouseToolOptionsTheme.startButton}
                        src={arrowDownSrc}
                        onSelect={() => setRadius((radius || 30) - 5)}
                        tooltip="Decrease Radius"
                        focusKey={FOCUS_DISABLED}
                    />
                    <input
                        type="number"
                        className={styles.cleanInput}
                        value={Math.round(radius || 30)}
                        onChange={(e) => setRadius(parseInt(e.target.value))}
                        onFocus={(e) => e.target.select()}
                    />
                    <ToolButton
                        className={mouseToolOptionsTheme.endButton}
                        src={arrowUpSrc}
                        onSelect={() => setRadius((radius || 30) + 5)}
                        tooltip="Increase Radius"
                        focusKey={FOCUS_DISABLED}
                    />
                </Section>

                <Section title="Filters">
                    <div className={styles.filterGrid}>
                        <FilterButton flag={1} label="Nets" src="Media/Game/Icons/Roads.svg" current={filters} />
                        <FilterButton flag={2} label="Buildings" src="Media/Game/Icons/ZoneResidential.svg" current={filters} />

                        <FilterButton
                            flag={4}
                            label="Trees"
                            src={treeIconSrc}
                            current={filters}
                            className={styles.greenIcon}
                        />

                        <FilterButton flag={16} label="Props" src="Media/Game/Icons/Props.svg" current={filters} />
                    </div>
                </Section>
            </div>
        );

        if (isOurToolActive) {
            result.props.children = [myUI];
        } else {
            const entryBtn = (
                <Section title="Radius Delete" key="radius-entry">
                    <ToolButton
                        src={iconPath}
                        onSelect={() => trigger("RadiusDelete", "ToggleTool")}
                        focusKey={FOCUS_DISABLED}
                    />
                </Section>
            );
            if (Array.isArray(result.props.children)) result.props.children.push(entryBtn);
            else result.props.children = [result.props.children, entryBtn];
        }
    }

    return result;
};

const FilterButton = ({ flag, label, src, current, className }: any) => {
    const { ToolButton, toolButtonTheme, FOCUS_DISABLED } = VanillaComponentResolver.instance;
    return (
        <ToolButton
            className={`${toolButtonTheme.button} ${className || ''}`}
            selected={(current & flag) === flag}
            onSelect={() => trigger("RadiusDelete", "SetFilter", flag)}
            src={src}
            tooltip={label}
            focusKey={FOCUS_DISABLED}
        />
    );
};