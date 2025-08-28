#!/usr/bin/env python3
"""
Script to replace 'Respire' with 'Respire' in:
- File contents
- File names
- Directory names
"""

import os
import sys
import shutil
from pathlib import Path
import argparse

def replace_in_file(file_path, old_text, new_text):
    """Replace text in a file."""
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            content = f.read()
        
        if old_text in content:
            new_content = content.replace(old_text, new_text)
            with open(file_path, 'w', encoding='utf-8') as f:
                f.write(new_content)
            return True
    except (UnicodeDecodeError, PermissionError) as e:
        print(f"  Skipping {file_path}: {e}")
    except Exception as e:
        print(f"  Error processing {file_path}: {e}")
    return False

def rename_item(old_path, old_text, new_text):
    """Rename a file or directory if it contains the old text."""
    path = Path(old_path)
    if old_text in path.name:
        new_name = path.name.replace(old_text, new_text)
        new_path = path.parent / new_name
        try:
            path.rename(new_path)
            print(f"  Renamed: {path} -> {new_path}")
            return new_path
        except Exception as e:
            print(f"  Error renaming {path}: {e}")
    return old_path

def process_directory(root_dir, old_text, new_text, dry_run=False):
    """Process all files and directories recursively."""
    root_path = Path(root_dir).resolve()
    
    # Collect all paths first to avoid issues with renaming during iteration
    all_files = []
    all_dirs = []
    
    for root, dirs, files in os.walk(root_path, topdown=False):
        # Skip hidden directories and common ignore patterns
        dirs[:] = [d for d in dirs if not d.startswith('.') and d not in ['node_modules', '__pycache__', 'venv', '.git']]
        
        for file in files:
            # Skip hidden files and binary files
            if not file.startswith('.') and not file.endswith(('.pyc', '.pyo', '.exe', '.dll', '.so', '.dylib')):
                all_files.append(Path(root) / file)
        
        for dir_name in dirs:
            all_dirs.append(Path(root) / dir_name)
    
    if dry_run:
        print("\n=== DRY RUN MODE - No changes will be made ===\n")
    
    # Process file contents
    print("Processing file contents...")
    modified_files = 0
    for file_path in all_files:
        if file_path.exists() and file_path.is_file():
            if dry_run:
                try:
                    with open(file_path, 'r', encoding='utf-8') as f:
                        content = f.read()
                    if old_text in content:
                        print(f"  Would modify: {file_path}")
                        modified_files += 1
                except:
                    pass
            else:
                if replace_in_file(file_path, old_text, new_text):
                    print(f"  Modified: {file_path}")
                    modified_files += 1
    
    print(f"\nModified {modified_files} file(s)")
    
    # Rename files
    print("\nRenaming files...")
    renamed_files = 0
    for file_path in all_files:
        if file_path.exists() and old_text in file_path.name:
            if dry_run:
                new_name = file_path.name.replace(old_text, new_text)
                print(f"  Would rename: {file_path.name} -> {new_name}")
                renamed_files += 1
            else:
                rename_item(file_path, old_text, new_text)
                renamed_files += 1
    
    print(f"Renamed {renamed_files} file(s)")
    
    # Rename directories (from deepest to shallowest)
    print("\nRenaming directories...")
    renamed_dirs = 0
    for dir_path in all_dirs:
        if dir_path.exists() and old_text in dir_path.name:
            if dry_run:
                new_name = dir_path.name.replace(old_text, new_text)
                print(f"  Would rename: {dir_path.name} -> {new_name}")
                renamed_dirs += 1
            else:
                rename_item(dir_path, old_text, new_text)
                renamed_dirs += 1
    
    print(f"Renamed {renamed_dirs} directory(ies)")
    
    # Also check and rename the root directory itself if needed
    if old_text in root_path.name:
        if dry_run:
            new_name = root_path.name.replace(old_text, new_text)
            print(f"\nWould rename root directory: {root_path.name} -> {new_name}")
        else:
            new_root = rename_item(root_path, old_text, new_text)
            if new_root != root_path:
                print(f"\nRenamed root directory: {root_path} -> {new_root}")

def main():
    parser = argparse.ArgumentParser(description='Replace "Respire" with "Respire" in files and directory names')
    parser.add_argument('path', nargs='?', default='.', help='Path to the directory to process (default: current directory)')
    parser.add_argument('--dry-run', action='store_true', help='Show what would be changed without making actual changes')
    parser.add_argument('--old', default='Respire', help='Text to replace (default: Respire)')
    parser.add_argument('--new', default='Respire', help='Replacement text (default: Respire)')
    
    args = parser.parse_args()
    
    if not os.path.exists(args.path):
        print(f"Error: Path '{args.path}' does not exist")
        sys.exit(1)
    
    print(f"Processing directory: {os.path.abspath(args.path)}")
    print(f"Replacing '{args.old}' with '{args.new}'")
    print("=" * 50)
    
    process_directory(args.path, args.old, args.new, args.dry_run)
    
    print("\n" + "=" * 50)
    print("Processing complete!")
    
    if args.dry_run:
        print("\nThis was a dry run. To apply changes, run without --dry-run flag")

if __name__ == "__main__":
    main()