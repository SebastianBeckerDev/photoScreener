# LumaPick review UI concept

Status: implemented and locally verified on Windows, 2026-11-09. The user approved three collapsible areas and removal of the crowded toolbar. The following decisions were confirmed during review:

| Decision | Agreed behavior |
| --- | --- |
| Export tree selection | Highlight a candidate; change the current source folder's binding only with **Use as export target**. |
| Image click | Request the available detailed preview and show its pixels at 100% around the pointer; another click returns to Fit. |
| Collapsed filmstrip | Hide thumbnails while retaining its slim footer with source/export links and the filter. |

## Layout and navigation

Left: a collapsible directory panel with **Source / Export target** tabs, a compact recent-directory picker (ten per role), the current directory's tree, an **Open folder** action and Refresh. The source tab opens a clicked source folder; the export tab browses candidates for the current source. The active source/export directory has a filled highlight, while a candidate export uses an outline. The export panel identifies which source folder the target belongs to.

Load directory children only when their branch expands. Probe reachability asynchronously; inaccessible entries retain their history and show both subdued styling and an unavailable label. Refresh retries discovery/availability without treating a disconnected drive as an empty folder. Context menus open the selected directory in Explorer/Finder. Source browsing continues to exclude designated exports as review sources.

Right: a collapsible photo-information panel containing the existing capture values and histogram. Bottom: a filmstrip with the existing centered selection and filename/pick state under each thumbnail, without a duplicated current-file/status title row. Panel-edge handles remain visible after collapse; remember their state and widths locally, and keep the image usable in smaller windows.

The slim, single-row footer retains source/export links (open Explorer/Finder), All/Picked/Unpicked and compact activity/error feedback. Links show directory names; tooltips contain their complete paths. The source tooltip also identifies Unknown review history or known assessed revisions, separately from picks. A missing export receives a short amber marker and an explanatory tooltip; browsing does not create it. Normal review does not repeat paths or optional-history bookkeeping in a second line. Loading, unavailable-directory and maintenance-error notices appear only when needed, with complete details in their tooltips. The old top-level Folders, Info, Strip, Fit, Project picks, Work, Retry and Cancel pending controls disappear. Directory actions move into the left panel; zoom controls move onto the image.

## Feedback and recovery

When thumbnails are hidden, show a small current-photo/pick badge on the image so Space still gives immediate visible feedback. Retained-image loading continues to identify the old displayed photo and newly selected target; on folder changes include enough folder context to distinguish equal filenames.

Running and failed copies remain accessible through the footer's compact status/popover, with contextual retry/cancel. Source/export changes and close retain their drain/error behavior. An image context-menu action can repair a missing NEF companion of an existing JPEG pick, replacing access through the permanent Retry button. No persistent Work panel is needed in normal review.

## Zoom and input

Click-to-detail executes on pointer release after distinguishing a click from a drag. A movement threshold prevents panning from resetting zoom. Wheel zoom remains anchored at the pointer; −, + and Fit appear at the lower-right image edge. The zoom percentage/loading status sits beside those controls and is hidden when fitted; there is no duplicate Fit badge above the photo. Buttons and thumbnail selection restore review keyboard focus, while tree/text/dialog input keeps its own navigation semantics.

100% refers to the detailed decoded preview at physical display pixels, including DPI/Retina. The initial fit-size bitmap may provide no extra detail: clicking must request the higher-resolution focused preview before completing the 100% intent, retain the image while loading, and reject late results after navigation. The existing 3840-pixel decode cap and 16×-Fit zoom bound remain the current limits. For small photos already shown at the available detail, Fit can remain unchanged. Original-resolution cropped inspection is a separate future enhancement.

The implementation retains the export/NEF protocols, generation guards, source protection and immediate keyboard feedback. Export binding changes are serialized, and a newer choice supersedes earlier validation without redirecting captured pending jobs.

## Preview priority

The selected photo's quick image and full preview precede unrelated visible thumbnails. Its thumbnail can share the freshly decoded quick image. Visible neighbors are ordered by distance in the latest selection direction, then by distance in the opposite direction; initial direction is right. Four upcoming quick previews are prefetched in that direction after foreground work. Visual attachment order does not reverse this queue.

A click requesting detail uses the urgent decoder directly while retaining the displayed image. It does not request the quick preview again or wait for queued thumbnails. Interrupted visible tiles remain eligible for retry. If an obsolete native detail call has not yet returned, the other fixed worker can display the newly selected photo. There remain two image workers and a bounded 64 MiB cache; the current detailed preview is protected from eviction by idle warming. Volumes without a supported native change token continue to bypass cache reuse, so a first detail decode or repeated reads on such storage can still take time.

Verification: 65 local Release desktop tests passed, including bidirectional ordering, an uninterruptible native-call simulation, actual tree/keyboard input, explicit export binding, panel persistence and physical-pixel click zoom. A real Windows window using eight generated JPEG fixtures was rendered and inspected. These checks do not establish photographic reference-workload timing or Mac execution.
