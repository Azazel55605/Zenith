> Original user-supplied outline, preserved on 2026-10-27. This is a scope record,
> not an implementation claim. [Ordered plan](../../MERIDIAN-PLAN.md).

# Settings planning

---

what needs to be in the settings app (Meridian)

## Categories

### Quick Settings
- One overview page on startup
- quick theme toggle (automatic mode based on time)
- quicklinks to wallpaper, theme, ...
- recently visited options

---

### Input & Output
#### Mouse and Touchpad
- Mouse
  - Device selector for multiple mice
  - left hand mode toggle
  - pointer speed
  - pointer acceleration
  - scroll speed
  - invert scroll
- touchpad
  - basically same as mouse

#### Keyboard
- Keyboard
  - Model selection
  - Num lock on startup
  - key repeat, delay, rate
  - a test area for typing
  - layout selecctor + switcher for multiple layouts
- Shortcuts
  - Settings for system shortcuts and system app shortcuts

#### Sound
- Playback devices
  - Test per device changing things like channels and possibly audio settings like bit depth and sample rate
- Recording devices
  - show pickup (per channel)
- Playback streams (apps currently outputting audio)
  - individual per app routing (would be nice to have)

#### Dispaly & Monitor (really important)
- Display Configuration
  - Resolution (important)
  - Scaling (important)
  - Orientation
  - Refresh rate
  - brightness (hard to test in a vm)
  - Screen tearing
- Night Light
  - On/Off/Auto
  - Strength
  - Temperature for day / night

#### Accessibility (comes at a later point)

---

### Connected Devices
#### Bluetooth (comes at a later point)

#### External Device Handling
- Automount on login / plug in (per device) ...

#### Printers (comes at a later point)

---

### Networking
#### Connections
- Connections list
  - connection name
  - IP settings
  - auto connect settings
  - priority settings
  - link negotiation
  - ...

---

### Appearance & Style
#### Wallpaper
- Plain Color
- Image
- Slideshow
- Wallpaper picker
  - Different Categories
  - Dynamic light/dark wallpapers

#### Colors / Themes
- Colors
  - Accent Color
  - Dark / Light mode
  - Switching options
- Application Style
  - Two or Three separate widget styles (for all widgets). maybe something more rounded. something with hard corners, ...
  - different button styles
  - ...
- Window Decorations
  - A couple of separate window decoration styles. The default windows one, the mac dots plus a couple extra
- Icons
  - A couple of icon sets for the system
- Cursors
  - A couple of cursor styles for the system
- Sounds
  - System sounds for interactions. A couple of different sound packs
- Splash screen
  - Enable an animation thats playing while the system transitions from the login manager to the desktop
- Login Manager
  - A couple of different styles for the login manager. again maybe one thats more like windows, one like mac, and a couple of different ones
- Boot Splash
  - If supported play a boot animation until the login manager appears

#### Texts & Fonts
- Fonts
  - Select System Font
  - Anti Aliasing
  - Hinting
  - ...
- Font Management
  - All Fonts
  - Personal Fonts
  - System Fonts
  - Install and Manage Fonts

#### Animations
- Global Animtaion Speed
- Enable / Disable separate animation (Window open/close, maximize, minimize, full screen, peek at desktop, virtual desktop switching, ...)

---

### Apps & Windows
#### Notifications
- DND
- Visibility Conditions
- Popups

#### Window Management
- Window Behaviour
  - Focus
    - Click to focus (in DE mode)
    - delay focus
    - raising window
  - Titlebar actions
    - Double Click to maximize
    - maximize button actions
  - movement
    - screen edge snap zone
    - window snap zone
- Task switcher
  - visualization
    - a couple of styles for how the task switcher should be displayed
  - content
    - sort order
    - only one window per application
    - filter by virtual desktops, monitors, ...
- Virtual Desktops
  - Create and manage virtual desktops

---

### Workspace
#### Genereal behaviour
- OSD popups for notifications / status changes
- Clicking a scrollbar move to location / scrolls page up / down
- clicking files and folders selects / opens them
- middle click to paste
- double click interval
- drag and drop ask what to do / always move when on the same device (drive)

#### Search
- File indexing
- data to index
- includes / excludes
- search plugins (math, power options, applications, ...) (search / runner like raycast or vicinae. maybe more usefull in the WM mode but search can extend to the start menu)

---

### Security & Privacy
#### Screen locking
- lock screen automatically + time
- delay before password required
- auto login

---

### Language & Time
#### Region & Language
- Language
- Numberformats
- Timeformats
- Currency
- Measurements
- Address style
- Phone Numbers
- Data & Storage

#### Date & Time
- automatic / manual
- timezone

#### Day-Night-Cycle
- automatic based on location (automatic detection / manual) / manual time selection

---

### System
#### About
- System information
- Software
- Hardware

#### Powermanagement
- Suspend
- auto dim after inactivity

#### Users
- manage users (add, delete, ...)

#### Autostart
- manage applications autostart behaviour

#### Session
- ask for confirmation on shutdown / reboot / logout
- session restore behaviour
