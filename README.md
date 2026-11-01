# PanelLayoutTo3D – EPLAN add-in: 2D panel layout (schakelkastopbouw) → 3D layout

Takes a 2D panel layout page and places its parts in the 3D layout (Pro Panel): on a mounting panel, a door or another
surface of a cabinet. It needs the **EPLAN API Extension** licence; a plain script cannot reach the 3D data model.

## Build

1. Open `PanelLayoutTo3D.vbproj` in Visual Studio 2022.
2. In the project file, set `EplanBin` to the Bin folder of your EPLAN Platform, e.g.
   `C:\Program Files\EPLAN\Platform\2026.0.3\Bin`.
   * The framework follows that folder. EPLAN up to 2026 runs on .NET Framework 4.8.1 (`Eplan.EplApi.*u.dll`), so the project targets `net481`. That needs the ".NET Framework 4.8.1 targeting pack" (Visual Studio Installer, Individual components).
   * EPLAN 2027 and later run on .NET 8 (`Eplan.EplApi.*Netu.dll`). The project then switches to `net8.0-windows` by itself.
   * When the folder is wrong, the build stops with "EPLAN API not found: ...".
3. Build in Release / x64. The result is `CdPc.EplAddin.PanelLayoutTo3D.dll`.
   * The name must contain `.EplAddin.`.
   * The EPLAN DLLs are referenced from the Bin folder and are not copied.

## Install

In EPLAN, go to File > Extensions > Add-ins (*Bestand > Uitbreidingen > Add-ins*), then Load, and pick the DLL.
* A button **"2D layout -> 3D"** appears under Extensions > API.
* The action is called `CdPcPanelLayoutTo3D`.

## Use

1. Select the 2D panel layout page in the page navigator, or open it.
   * Optional: select some parts on the page first. Only those are ticked.
2. Click the button. The dialog fills itself from the project and the parts database. Its top has two tabs.

### Standard tab

* **Cabinet**, in this order:
  * the cabinet devices of the project that are not in 3D yet (device tag + part, e.g. +TD_204-PANEL1). The part must be a cabinet in the parts database;
  * the cabinets already in the 3D layout;
  * mounting panels that are in no cabinet;
  * "All cabinets from parts DB" adds every other part of the product group the cabinets are in (the whole "Bouwgroep": cabinets, enclosures, 19 inch, mounting hook-up), with its sub group in brackets. Accessories, doors, mounting panels, rails and ducts stay out.
  * The cabinet with the page's location (TD_203 for +TD_203/9) is chosen by default; one already in 3D comes first.
  * A new cabinet is built from its part's 3D macro (body, doors, mounting panel) in the chosen installation space, to the right of what is already there, and gets the device's tag. Its parts without a name get the cabinet's tag too.
* **Surface (panel, door, side)**: every part of that cabinet that things are mounted on, each with its planes.
  * Named by what it is and where it is: "Mounting panel", "Door left" / "Door right" (two front doors), "Door 1 (from the left)" ... (more), "Side wall, left", "Side door, right", "Rear wall", "Roof" ..., with the plane's side (outside / inside).
  * The default is the plane whose size fits the 2D plate outline: a panel layout page gets the mounting panel, a door layout page gets the outside of the door.
* **Installation space** (new cabinet only): follows the chosen cabinet. For +TD_204-PANEL1 the existing space TD_204, else "New installation space: TD_204". A cabinet from the parts database keeps the space of the page's location. The structure (+TD_204) is set on a new space.
* **2D mounting plate**: its lower left corner becomes the lower left corner of the 3D surface.
* **Place**: Ducts / Rails / Devices / Drill holes (not saved). Only the ticked rows of these types are placed, so ducts and rails can go first and the devices after.

### Settings tab (saved)

* **Scale**: "from the plate" keeps 1 when the plate and the surface differ by less than 5 %, because the 2D page is in real mm. Mirror left / right, top / bottom. **Shift right / up** moves everything on the surface.
* **2D page**: rails from lines in a colour (1 = red), rail ends onto ducts within ... mm, ducts from filled rectangles. "Read page again" after changing these.
* **Options**: devices onto the rail under them, skip devices already in 3D, give the 3D part the device tag.
* **Drill holes**: a round drill hole (diameter, default 1 mm) in the mounting panel at the middle of each 2D object without a part (fixing points drawn as macros). Not twice at the same spot.
* **Default duct make** (RIT, SES, ...): the first choice for duct rectangles of its width. **Default DIN rail**: the first choice for rail lines. "(the one used most in the project)" otherwise.

The settings, the window size, the Turn column and the log are kept in `%APPDATA%\CD-PC\ePLAN`
(`PanelLayoutTo3D.settings.cfg`, `PanelLayoutTo3D.turns.txt`, `PanelLayoutTo3D.log`).

### Grid

One row per object:
* part placements: the type follows the product group in the parts database;
* rail lines: the part is chosen from the rails in the parts database;
* duct rectangles: the part is chosen from the ducts in the parts database that are as wide as the rectangle (sizes shown as w / h / d, ducts as width x depth);
* objects without a part: "Drill hole" when that setting is on.
* In the choice lists the default from the settings comes first, then the part used most in the project (marked `*`).
* **Turn** ("", 90, 180, 270, flip): an extra turn after placing, for every row with that part number, remembered for next time. 90 / 180 / 270 on the surface, flip = front and back swapped.

3. **Convert to 3D**.
   * One undo step: Edit > Undo removes everything that was placed.
   * The log appears in the dialog and is appended to `PanelLayoutTo3D.log`; "Save log to file..." saves it as a .log file of your choice.

## What it does (checked on 26-0266 against its 3D layout)

* **Coordinates:** the 2D coordinates are real mm, also on 1:9 pages.
  * The front surface of a mounting panel has its origin in the lower left corner, x to the right, y up, the same as the page.
  * The 2D outline of a part is the size of the 3D part seen from the front.
* **Order:** ducts, rails, devices (large ones first), drill holes.
  * Ducts: length = the rectangle, centred on it, placed by their outline.
  * Rails, along their line. A rail line stops 5 mm short of the ducts, but in the cabinet the rail runs from duct to duct, so each end within the set distance of a duct, or of the plate edge, is moved onto it. A rail EPLAN cannot build is replaced by the next one from the parts database (logged).
  * Devices, centred on their 2D outline:
    * over a rail: snapped onto the rail's line with their own mounting point as EPLAN does by hand, slid along it to their 2D position; tipped back up when EPLAN's snap left them lying (height in the depth);
    * over a larger device with a mounting line of its own or on top of it (the S7-1500 modules on their mounting rail): onto that part, their back on its front;
    * else onto the surface with their mounting point (G1 -> MP), lined up with the surface (front out, top up); on a mounting panel the back goes on the panel. On a door a push button's or HMI's body goes through the door.
* **Two doors:** a 2D door outline over two doors side by side (+TD_203/9: 1199 wide over 592 + 625) is not scaled down: each object goes onto the door its middle lies on.
* **Device tags:** a device gets its device tag on the 3D part, so EPLAN sees it as placed.
* **Re-running:** devices that already have a 3D placement (same tag and part) are skipped, and rails / ducts placed by an earlier run of the dialog too. After an undo they are placed again.
* **Zero position setting:** the project setting for horizontal / vertical component placement (`CabinetGui.ZeroCut.*`) does not affect the result, because everything is placed by its absolute position on the surface.

## Known limits

* **Mate directions:** the API reports a mounting point's frame turned 180° about its x axis compared with the one EPLAN shows; the add-in corrects for that. When a part's mounting point cannot be read at all, the part's own axes are used ("by the part's own axes" in the log).
* **Product group names:** the enum names are matched on "rail", "duct" and "mountingpanel". If your database names them differently, a rail or duct placement shows up as "Device"; change the type in the grid.
* **Part dimensions:** width / height / depth are read as `ARTICLE_WIDTH` / `ARTICLE_HEIGHT` / `ARTICLE_DEPTH`. Without a width all ducts are offered instead of only the matching width.
* **Parts that reach through the cabinet** (a rotary handle with its shaft to a switch on the mounting panel) cannot be placed sensibly from a door page: set them to Skip and place them by hand.
