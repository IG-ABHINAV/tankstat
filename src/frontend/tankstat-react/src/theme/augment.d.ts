// What the Tankstat theme adds to MUI's types: soft tones on every colour, a neutral (gray) colour, the soft and ghost button and
// chip variants, glass paper and one more breakpoint. Types only (erasableSyntaxOnly): the values live in theme/.
import type {} from '@mui/material/themeCssVarsAugmentation'
import type {} from '@mui/x-data-grid/themeAugmentation'
import type {} from '@mui/x-date-pickers/themeAugmentation'
import type {} from '@mui/x-charts/themeAugmentation'

declare module '@mui/material/styles' {
  interface PaletteColor {
    /** A tinted surface in this colour (Radix step a3), e.g. a soft button or chip. */
    soft: string
    /** The soft surface under the pointer (Radix a4). */
    softHover: string
    /** Readable text in this colour on the app's background or its soft surface (Radix step 11). */
    softText: string
    /** The fill under `contrastText` (a contained button, a badge, a solid chip): at least 4.5:1 with it (#235). */
    solid: string
    /** The solid fill under the pointer, at least 4.5:1 with `contrastText` too; also MUI's `dark`. */
    solidHover: string
  }
  interface SimplePaletteColorOptions {
    soft?: string
    softHover?: string
    softText?: string
    solid?: string
    solidHover?: string
  }
  interface Palette {
    /** The gray of the app (Radix "sand"): high-contrast solid, soft surfaces, secondary text. */
    neutral: PaletteColor
  }
  interface PaletteOptions {
    neutral?: SimplePaletteColorOptions
  }
  interface BreakpointOverrides {
    /** Wide desktops (1640 px). */
    xxl: true
  }
}

declare module '@mui/material/Button' {
  interface ButtonPropsVariantOverrides {
    soft: true
    ghost: true
  }
  interface ButtonPropsColorOverrides {
    neutral: true
  }
}

declare module '@mui/material/IconButton' {
  interface IconButtonPropsColorOverrides {
    neutral: true
  }
}

declare module '@mui/material/Chip' {
  interface ChipPropsVariantOverrides {
    soft: true
    solid: true
  }
  interface ChipPropsColorOverrides {
    neutral: true
  }
}

declare module '@mui/material/Alert' {
  interface AlertPropsColorOverrides {
    neutral: true
  }
}

declare module '@mui/material/CircularProgress' {
  interface CircularProgressPropsColorOverrides {
    neutral: true
  }
}

declare module '@mui/material/LinearProgress' {
  interface LinearProgressPropsColorOverrides {
    neutral: true
  }
}
