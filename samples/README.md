# Sample labels

These are the 25 sample labels built into LabelScope. Open them from the **...** menu, under **Sample labels**, or press **Try a sample label** in the empty window. Each file here is the same ZPL that the app draws, so you can also open one with **Open ZPL file**, or send it to a real Zebra printer to compare its print with what LabelScope shows.

All of them are written for 203 dpi, and each one sets its own label size.

## Shipping

| No. | Sample | What it shows |
|---|---|---|
| 01 | [Parcel shipping label](01-parcel-shipping-label.zpl) | A 4 x 6 inch parcel label: ship-from and ship-to boxes, a picture sent inside the label, a service band in reversed text, a Code 128 tracking number and a GS1-128 postal code. |
| 02 | [GS1-128 carton label with SSCC](02-gs1-carton-label.zpl) | The three sections of a GS1 logistics label: ship-from and ship-to, the contents with a GS1-128 symbol, and the SSCC that identifies the carton. |
| 03 | [Pallet label](03-pallet-label.zpl) | A black header, "PALLET 1 OF 3" turned along the edge, and two GS1-128 symbols: the SSCC and the contained item with count, batch and best-before date. |
| 04 | [Freight (LTL) label](04-freight-ltl-label.zpl) | Very large terminal and door codes that can be read from across the dock, a rotated piece count and the PRO number as a Code 128 symbol. |
| 05 | [Return label](05-return-label.zpl) | The return address, the return authorization number as a Code 128 symbol and a QR code that opens a return page. |
| 06 | [Packing slip](06-packing-slip.zpl) | The same parcel-box picture, a table whose item lines wrap inside a field block, and a QR code with a link to the order. |
| 07 | [ITF-14 case label](07-itf14-case-label.zpl) | The bar code on the outer case of a retail product: an ITF-14 symbol inside bearer bars. |

## Warehouse

| No. | Sample | What it shows |
|---|---|---|
| 08 | [Pick ticket on a saved template](08-pick-ticket-saved-template.zpl) | Three blocks that make one label: an empty form saved in printer memory, the label drawn on it, and the template deleted. |
| 09 | [Bin location label](09-bin-location-label.zpl) | A shelf label: an arrow, the location in large letters and the same location as a Code 128 symbol. |
| 10 | [License plate (LPN) label](10-license-plate-lpn.zpl) | A label for a pallet or tote: the license plate number as a Code 128 symbol, with the item, quantity and location. |
| 11 | [Receiving label](11-receiving-label.zpl) | The label put on goods at the receiving dock: the purchase order number as a Code 93 symbol, the supplier and the dock. |
| 12 | [Rack label](12-rack-label.zpl) | A rack upright label: the aisle number in very large rotated letters, the rack position and an arrow. |
| 13 | [Inventory count tag](13-inventory-count-tag.zpl) | A stock-count tag: the tag number as a LOGMARS (Code 39) bar code and blank lines to write on by hand. |

## Retail

| No. | Sample | What it shows |
|---|---|---|
| 14 | [UPC-A shelf price label](14-upc-a-price-label.zpl) | A shelf-edge label: the product name, a large price, a UPC-A bar code and a shorter MSI bar code for the stock count. |
| 15 | [EAN-13 product label (Spanish)](15-ean-13-product-label.zpl) | A pack label with Spanish accents (UTF-8) and an EAN-13 bar code. |
| 16 | [Small UPC-E label](16-upc-e-small-label.zpl) | A tiny label for a small package, with a UPC-E bar code, the short form of UPC-A. |
| 17 | [EAN-8 price tag](17-ean-8-price-tag.zpl) | A small tag for a product too small for an EAN-13: the name, a large price and an EAN-8 bar code. |
| 18 | [Clothing tag](18-clothing-tag.zpl) | A hanging tag with a punched hole, a very large size letter, care instructions that wrap inside a field block and a UPC-A bar code. |

## Manufacturing

| No. | Sample | What it shows |
|---|---|---|
| 19 | [Serial-number labels](19-serial-number-labels.zpl) | One job that prints five labels, each with the next serial number (keeping its leading zeros) as a Code 128 bar code. |
| 20 | [Automotive part label](20-automotive-part-label.zpl) | Four Code 39 bar codes in titled boxes in the style of the AIAG shipping label: part, quantity, supplier and serial number. |
| 21 | [Asset tag](21-asset-tag.zpl) | A QR code that opens an asset page, the owner, a large asset number and a Code 11 bar code. |
| 22 | [Lot and expiry label](22-lot-expiry-label.zpl) | The product, the lot and the expiry date in large print, and a GS1-128 bar code that holds the item number, the expiry date and the lot. |

## Healthcare

| No. | Sample | What it shows |
|---|---|---|
| 23 | [Pharmacy label with GS1 Data Matrix](23-pharmacy-datamatrix-label.zpl) | A small pack label with a GS1 Data Matrix that holds the item number, expiry date, lot and serial number. |
| 24 | [Specimen tube label](24-specimen-tube-label.zpl) | A small label for a lab tube: a made-up patient, the kind of specimen, the collection time and the accession number as a Codabar bar code. |

## Documents

| No. | Sample | What it shows |
|---|---|---|
| 25 | [Shipping document with PDF417](25-shipping-document-pdf417.zpl) | A full 4 x 6 inch document: titled boxes, paragraphs wrapped inside field blocks and a PDF417 symbol that holds a short summary of the shipment. |

## Good to know

- **All the data is made up.** Names, addresses, phone numbers, product numbers and bar code data are invented for these samples. They do not belong to any real person, company or product.
- **A sample never changes your printer's memory.** Each sample is drawn in a memory of its own, at 203 dpi, so opening one does not change the label size, the graphics or the settings that your real jobs use.
- **Compare with a real printer.** Send any of these files to a Zebra printer and put the printed label next to the one LabelScope shows. The letter shapes differ a little, but sizes, positions and bar codes should match.
