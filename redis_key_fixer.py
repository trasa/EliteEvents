import redis
import re

# Configuration
REDIS_HOST = 'localhost'
REDIS_PORT = 6379
REDIS_DB = 0
REDIS_PASSWORD = None  # Set if needed

# Pattern to match system keys
KEY_PATTERN = 'system:*'

def normalize_system_name(system_name):
    """Convert system name to uppercase."""
    return system_name.upper()

def parse_key(key):
    """Parse a key and extract the system name and suffix."""
    # Match pattern: system:SYSTEMNAME:station:ANYTHING (check this first - more specific)
    match = re.match(r'^system:(.+?):station:(.+)$', key)
    if match:
        return match.group(1), f'station:{match.group(2)}'
    
    # Match pattern: system:SYSTEMNAME:stations
    match = re.match(r'^system:(.+):stations$', key)
    if match:
        return match.group(1), 'stations'
    
    return None, None

def create_normalized_key(system_name, suffix):
    """Create a key with normalized system name."""
    return f"system:{normalize_system_name(system_name)}:{suffix}"

def migrate_keys(dry_run=True, batch_size=1000, use_tls=False):
    """
    Migrate Redis keys to have uppercase system names.
    
    Args:
        dry_run: If True, only print what would be done without making changes
        batch_size: Number of keys to process before printing progress
        use_tls: If True, use TLS/SSL connection
    """
    # Connect to Redis
    r = redis.Redis(
        host=REDIS_HOST,
        port=REDIS_PORT,
        db=REDIS_DB,
        password=REDIS_PASSWORD,
        decode_responses=True,
        ssl=use_tls
    )
    
    print(f"Connected to Redis at {REDIS_HOST}:{REDIS_PORT}")
    print(f"Mode: {'DRY RUN' if dry_run else 'LIVE MIGRATION'}")
    print(f"Scanning for keys matching: {KEY_PATTERN}\n")
    
    processed = 0
    migrated = 0
    skipped = 0
    errors = 0
    
    # Use SCAN to iterate through keys without blocking Redis
    cursor = 0
    while True:
        cursor, keys = r.scan(cursor, match=KEY_PATTERN, count=batch_size)
        
        for key in keys:
            processed += 1
            
            # Extract system name and suffix
            system_name, suffix = parse_key(key)
            if not system_name or not suffix:
                print(f"Warning: Could not parse key: {key}")
                errors += 1
                continue
            
            # Generate normalized key
            new_key = create_normalized_key(system_name, suffix)
            
            # Skip if already normalized
            if key == new_key:
                skipped += 1
                if processed % batch_size == 0:
                    print(f"Progress: {processed} keys processed ({migrated} migrated, {skipped} skipped, {errors} errors)")
                continue
            
            # Perform migration
            if dry_run:
                print(f"Would rename: {key} -> {new_key}")
                migrated += 1
            else:
                try:
                    # Check if target key already exists
                    if r.exists(new_key):
                        print(f"Warning: Target key already exists: {new_key}")
                        print(f"  Skipping: {key}")
                        errors += 1
                        continue
                    
                    # Rename the key
                    r.rename(key, new_key)
                    migrated += 1
                    
                    if migrated % 100 == 0:
                        print(f"Migrated {migrated} keys...")
                        
                except redis.RedisError as e:
                    print(f"Error migrating {key}: {e}")
                    errors += 1
            
            if processed % batch_size == 0:
                print(f"Progress: {processed} keys processed ({migrated} migrated, {skipped} skipped, {errors} errors)")
        
        # Break when scan is complete
        if cursor == 0:
            break
    
    # Final summary
    print("\n" + "="*60)
    print("MIGRATION SUMMARY")
    print("="*60)
    print(f"Total keys processed: {processed}")
    print(f"Keys migrated: {migrated}")
    print(f"Keys already normalized: {skipped}")
    print(f"Errors: {errors}")
    print("="*60)

if __name__ == "__main__":
    import argparse
    
    parser = argparse.ArgumentParser(description='Normalize Redis key casing')
    parser.add_argument('--live', action='store_true', 
                        help='Actually perform the migration (default is dry-run)')
    parser.add_argument('--host', default='localhost', 
                        help='Redis host')
    parser.add_argument('--port', type=int, default=6379, 
                        help='Redis port')
    parser.add_argument('--db', type=int, default=0, 
                        help='Redis database number')
    parser.add_argument('--password', 
                        help='Redis password')
    parser.add_argument('--tls', action='store_true',
                        help='Use TLS/SSL connection')
    
    args = parser.parse_args()
    
    # Update configuration
    REDIS_HOST = args.host
    REDIS_PORT = args.port
    REDIS_DB = args.db
    REDIS_PASSWORD = args.password
    
    # Run migration
    migrate_keys(dry_run=not args.live, use_tls=args.tls)
